using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SupportTicketing.Application.Abstractions;

namespace SupportTicketing.Api.Hubs;

/// <summary>
/// Real-time channel for ticket activity and notifications.
/// </summary>
/// <remarks>
/// <para>
/// Authorization is enforced on the hub, not merely on the connection. Clients are
/// placed into groups the server chooses from their validated token — a client cannot
/// ask to join an arbitrary group, because that would let anyone subscribe to another
/// organization's traffic simply by guessing a name.
/// </para>
/// <para>
/// The hub carries notices, never authoritative data. A message says "ticket X
/// changed"; the client then re-fetches through the normal API, where the usual
/// permission and scope checks apply. Pushing ticket bodies down this channel would
/// bypass those checks and risk delivering an internal note to a requester.
/// </para>
/// </remarks>
[Authorize]
public sealed class TicketHub(ICurrentUser currentUser, IAppDbContext db) : Hub
{
    /// <summary>Group carrying events for one organization.</summary>
    public static string OrganizationGroup(Guid organizationId) => $"org:{organizationId}";

    /// <summary>Group carrying events for one user, used for their notifications.</summary>
    public static string UserGroup(Guid userId) => $"user:{userId}";

    /// <summary>Group carrying events for one ticket, joined while its page is open.</summary>
    public static string TicketGroup(Guid ticketId) => $"ticket:{ticketId}";

    public override async Task OnConnectedAsync()
    {
        // Group membership comes from the token, never from client input.
        if (currentUser.OrganizationId is { } organizationId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, OrganizationGroup(organizationId));
        }

        if (currentUser.UserId is { } userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribes to one ticket while the user has it open.
    /// </summary>
    /// <remarks>
    /// The caller supplies a ticket id, so it must be checked. Without the lookup any
    /// authenticated user could subscribe to any ticket in any organization by
    /// guessing an identifier and receive a change notice for it.
    /// </remarks>
    public async Task SubscribeToTicket(Guid ticketId)
    {
        var permitted = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .AnyAsync(db.Tickets.Where(t => t.Id == ticketId));

        if (!permitted)
        {
            // Silently ignored rather than answered. Confirming that a ticket exists
            // but is not yours is the same identifier-enumeration leak the REST
            // endpoints avoid by returning 404.
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
    }

    public Task UnsubscribeFromTicket(Guid ticketId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
}
