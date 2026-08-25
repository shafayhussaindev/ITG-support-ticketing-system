using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.UnitTests.Tickets;

public class TicketWorkflowTests
{
    private static Ticket TicketWith(TicketStatus status, string? resolution = null, Guid? staffId = null) => new()
    {
        TicketNumber = "TKT-2026-000001",
        Subject = "Printer offline",
        Description = "The floor printer stopped responding.",
        Status = status,
        ResolutionSummary = resolution,
        AssignedStaffId = staffId,
    };

    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Assigned)]
    [InlineData(TicketStatus.New, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Assigned, TicketStatus.InProgress)]
    [InlineData(TicketStatus.InProgress, TicketStatus.WaitingForRequester)]
    [InlineData(TicketStatus.InProgress, TicketStatus.Resolved)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Closed)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Reopened)]
    [InlineData(TicketStatus.Closed, TicketStatus.Reopened)]
    [InlineData(TicketStatus.Reopened, TicketStatus.InProgress)]
    public void Legitimate_transitions_are_permitted(TicketStatus from, TicketStatus to)
    {
        TicketWorkflow.CanTransition(from, to).ShouldBeTrue($"{from} to {to} should be allowed");
    }

    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Resolved)]      // nobody has looked at it
    [InlineData(TicketStatus.New, TicketStatus.Closed)]        // skips resolution entirely
    [InlineData(TicketStatus.InProgress, TicketStatus.Closed)] // closing without resolving
    [InlineData(TicketStatus.Closed, TicketStatus.InProgress)] // must be reopened first
    [InlineData(TicketStatus.Cancelled, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Cancelled, TicketStatus.Reopened)]
    public void Shortcuts_that_would_skip_a_required_step_are_rejected(TicketStatus from, TicketStatus to)
    {
        TicketWorkflow.CanTransition(from, to).ShouldBeFalse($"{from} to {to} should be blocked");
    }

    [Fact]
    public void An_invalid_transition_throws_with_both_ends_named()
    {
        var exception = Should.Throw<InvalidStatusTransitionException>(
            () => TicketWorkflow.EnsureCanTransition(TicketStatus.New, TicketStatus.Closed));

        exception.From.ShouldBe(nameof(TicketStatus.New));
        exception.To.ShouldBe(nameof(TicketStatus.Closed));
    }

    [Fact]
    public void Cancelled_is_the_only_terminal_state()
    {
        // Closed must not be terminal: a requester who finds the problem recurring has
        // to be able to reopen rather than raise a disconnected new ticket.
        TicketWorkflow.IsTerminal(TicketStatus.Cancelled).ShouldBeTrue();
        TicketWorkflow.IsTerminal(TicketStatus.Closed).ShouldBeFalse();

        foreach (var status in Enum.GetValues<TicketStatus>().Where(s => s != TicketStatus.Cancelled))
        {
            TicketWorkflow.IsTerminal(status).ShouldBeFalse($"{status} should not be terminal");
        }
    }

    [Fact]
    public void Every_status_has_an_entry_in_the_graph()
    {
        // A missing entry would throw a KeyNotFoundException mid-transition rather than
        // reporting a clean validation failure.
        foreach (var status in Enum.GetValues<TicketStatus>())
        {
            Should.NotThrow(() => TicketWorkflow.AllowedFrom(status));
        }
    }

    [Fact]
    public void Every_non_terminal_status_can_still_reach_a_terminal_one()
    {
        // Guards against a workflow dead end: a ticket that can never be closed or
        // cancelled would sit in the backlog forever.
        foreach (var start in Enum.GetValues<TicketStatus>())
        {
            if (TicketWorkflow.IsTerminal(start))
            {
                continue;
            }

            Reaches(start, TicketStatus.Closed).ShouldBeTrue($"{start} cannot reach Closed");
        }
    }

    private static bool Reaches(TicketStatus from, TicketStatus target)
    {
        var seen = new HashSet<TicketStatus>();
        var queue = new Queue<TicketStatus>([from]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (current == target)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var next in TicketWorkflow.AllowedFrom(current))
            {
                queue.Enqueue(next);
            }
        }

        return false;
    }

    [Fact]
    public void Reassignment_is_expressed_as_Assigned_to_Assigned()
    {
        // Moving a ticket between staff does not change the phase of the work, so the
        // graph has to permit the self-edge or every reassignment would be rejected.
        TicketWorkflow.CanTransition(TicketStatus.Assigned, TicketStatus.Assigned).ShouldBeTrue();
    }

    [Fact]
    public void Resolving_without_a_summary_is_refused()
    {
        var ticket = TicketWith(TicketStatus.InProgress, resolution: null);

        var exception = Should.Throw<BusinessRuleException>(
            () => TicketWorkflow.EnsureEntryRequirements(ticket, TicketStatus.Resolved));

        exception.Code.ShouldBe("ticket.resolution_summary_required");
    }

    [Fact]
    public void Resolving_with_a_summary_is_allowed()
    {
        var ticket = TicketWith(TicketStatus.InProgress, resolution: "Replaced the print spooler service.");

        Should.NotThrow(() => TicketWorkflow.EnsureEntryRequirements(ticket, TicketStatus.Resolved));
    }

    [Fact]
    public void Whitespace_does_not_count_as_a_resolution_summary()
    {
        var ticket = TicketWith(TicketStatus.InProgress, resolution: "   ");

        Should.Throw<BusinessRuleException>(
            () => TicketWorkflow.EnsureEntryRequirements(ticket, TicketStatus.Resolved));
    }

    [Fact]
    public void Marking_a_ticket_assigned_requires_an_owner()
    {
        var ticket = TicketWith(TicketStatus.New);

        var exception = Should.Throw<BusinessRuleException>(
            () => TicketWorkflow.EnsureEntryRequirements(ticket, TicketStatus.Assigned));

        exception.Code.ShouldBe("ticket.assignee_required");
    }

    [Fact]
    public void Closing_is_refused_unless_the_ticket_was_resolved_first()
    {
        var ticket = TicketWith(TicketStatus.InProgress);

        var exception = Should.Throw<BusinessRuleException>(
            () => TicketWorkflow.EnsureEntryRequirements(ticket, TicketStatus.Closed));

        exception.Code.ShouldBe("ticket.close_requires_resolution");
    }

    [Fact]
    public void Auto_close_from_waiting_for_requester_is_permitted()
    {
        // The auto-close job finishes tickets the requester stopped responding to, so
        // this path has to exist even though it bypasses an explicit resolution.
        var ticket = TicketWith(TicketStatus.WaitingForRequester);

        TicketWorkflow.CanTransition(TicketStatus.WaitingForRequester, TicketStatus.Closed).ShouldBeTrue();
        Should.NotThrow(() => TicketWorkflow.EnsureEntryRequirements(ticket, TicketStatus.Closed));
    }

    [Theory]
    [InlineData(TicketStatus.WaitingForRequester, true)]
    [InlineData(TicketStatus.WaitingForThirdParty, true)]
    [InlineData(TicketStatus.InProgress, false)]
    [InlineData(TicketStatus.Escalated, false)]
    public void Only_genuine_external_waits_are_pausable(TicketStatus status, bool expected)
    {
        // The SLA clock may pause while waiting on someone outside the support team,
        // never while the delay is internal. Escalated is explicitly not pausable —
        // an escalation is support's own backlog.
        TicketWorkflow.IsWaitingOnOthers(status).ShouldBe(expected);
    }

    [Theory]
    [InlineData(TicketStatus.Resolved, true)]
    [InlineData(TicketStatus.Closed, true)]
    [InlineData(TicketStatus.Cancelled, true)]
    [InlineData(TicketStatus.InProgress, false)]
    [InlineData(TicketStatus.Reopened, false)]
    public void The_resolution_clock_stops_only_at_resolution_or_beyond(TicketStatus status, bool expected)
    {
        TicketWorkflow.IsResolvedOrBeyond(status).ShouldBe(expected);
    }
}
