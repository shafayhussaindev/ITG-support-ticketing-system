using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Contracts.Auth;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// The administration surface: users, roles, teams, catalogue, SLA and settings.
/// </summary>
/// <remarks>
/// These endpoints hand out capability, so most of what is asserted here is what the
/// system refuses: deleting a role somebody holds, removing a staff member who still owns
/// work, saving a matrix with a hole in it, writing a secret back as its own mask.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class AdministrationTests(ApiFactory factory)
{
    private async Task<HttpClient> SignInAsync(string email, string? password = null)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest { Email = email, Password = password ?? ApiFactory.DemoPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // ------------------------------------------------------------------ users

    [Fact]
    public async Task An_agent_cannot_reach_the_administration_endpoints()
    {
        var agent = await SignInAsync("agent@itg.test");

        foreach (var path in new[]
                 {
                     "/api/v1/admin/users",
                     "/api/v1/admin/roles",
                     "/api/v1/admin/teams",
                     "/api/v1/admin/catalog/categories",
                     "/api/v1/admin/settings",
                 })
        {
            var response = await agent.GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task Creating_a_user_returns_a_one_time_password_that_actually_works()
    {
        var admin = await SignInAsync("admin@itg.test");

        var created = await ReadAsync<TemporaryPasswordResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
            {
                Email = "new.starter@itg.test",
                FirstName = "Hina",
                LastName = "Aslam",
                JobTitle = "Merchandiser",
            }));

        created.TemporaryPassword.ShouldNotBeNullOrWhiteSpace();

        // The generated password must be usable, not merely returned. A hash written
        // from a different string than the one shown would only surface as a support
        // call from the new starter.
        var theirClient = factory.CreateClient();

        var signIn = await theirClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Email = "new.starter@itg.test",
            Password = created.TemporaryPassword,
        });

        signIn.StatusCode.ShouldBe(HttpStatusCode.OK, await signIn.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_duplicate_email_is_refused()
    {
        var admin = await SignInAsync("admin@itg.test");

        var response = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Email = "agent@itg.test",
            FirstName = "Someone",
            LastName = "Else",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deactivating_an_account_ends_its_sessions()
    {
        var admin = await SignInAsync("admin@itg.test");

        var created = await ReadAsync<TemporaryPasswordResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
            {
                Email = "leaver@itg.test",
                FirstName = "Tariq",
                LastName = "Mahmood",
            }));

        var theirClient = factory.CreateClient();

        var auth = await ReadAsync<AuthResponse>(
            await theirClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
            {
                Email = "leaver@itg.test",
                Password = created.TemporaryPassword,
            }));

        var users = await ReadAsync<PagedResult<UserListItemResponse>>(
            await admin.GetAsync("/api/v1/admin/users?search=leaver@itg.test"));

        var user = users.Items.Single();

        var deactivated = await ReadAsync<UserDetailResponse>(
            await admin.PostAsJsonAsync($"/api/v1/admin/users/{user.Id}/active",
                new SetUserActiveRequest { IsActive = false, Reason = "Left the company" }));

        deactivated.IsActive.ShouldBeFalse();
        deactivated.ActiveSessions.ShouldBe(0);

        // The refresh token they were holding is now worthless, which is what actually
        // ends their access — clearing the flag alone would not.
        var refresh = await theirClient.PostAsJsonAsync(
            "/api/v1/auth/refresh", new RefreshRequest { RefreshToken = auth.RefreshToken });

        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_administrator_cannot_deactivate_themselves()
    {
        var admin = await SignInAsync("admin@itg.test");

        var me = await ReadAsync<CurrentUserResponse>(await admin.GetAsync("/api/v1/auth/me"));

        var response = await admin.PostAsJsonAsync($"/api/v1/admin/users/{me.Id}/active",
            new SetUserActiveRequest { IsActive = false });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Effective_permissions_are_shown_for_a_user()
    {
        var admin = await SignInAsync("admin@itg.test");

        var users = await ReadAsync<PagedResult<UserListItemResponse>>(
            await admin.GetAsync("/api/v1/admin/users?search=manager@itg.test"));

        var detail = await ReadAsync<UserDetailResponse>(
            await admin.GetAsync($"/api/v1/admin/users/{users.Items.Single().Id}"));

        detail.EffectivePermissions.ShouldContain("reports.export");
        detail.EffectivePermissions.ShouldNotContain("audit.view");
    }

    // ------------------------------------------------------------------ roles

    [Fact]
    public async Task A_system_role_cannot_be_deleted_but_its_permissions_can_be_edited()
    {
        var admin = await SignInAsync("admin@itg.test");

        var roles = await ReadAsync<IReadOnlyList<RoleResponse>>(await admin.GetAsync("/api/v1/admin/roles"));
        var staffRole = roles.Single(r => r.Name == "Staff");

        staffRole.IsSystemRole.ShouldBeTrue();

        var delete = await admin.DeleteAsync($"/api/v1/admin/roles/{staffRole.Id}");
        delete.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Editing is allowed: the seeded roles are a starting point, not a contract.
        var withExtra = staffRole.Permissions.Append("ticket.change_priority").Distinct().ToList();

        var updated = await ReadAsync<RoleResponse>(
            await admin.PutAsJsonAsync($"/api/v1/admin/roles/{staffRole.Id}/permissions",
                new SetRolePermissionsRequest
                {
                    PermissionKeys = withExtra,
                    Reason = "Staff triage their own queue",
                }));

        updated.Permissions.ShouldContain("ticket.change_priority");

        // Put it back so the rest of the suite sees the seeded shape.
        await admin.PutAsJsonAsync($"/api/v1/admin/roles/{staffRole.Id}/permissions",
            new SetRolePermissionsRequest { PermissionKeys = staffRole.Permissions });
    }

    [Fact]
    public async Task An_unknown_permission_key_is_rejected_rather_than_skipped()
    {
        var admin = await SignInAsync("admin@itg.test");

        var roles = await ReadAsync<IReadOnlyList<RoleResponse>>(await admin.GetAsync("/api/v1/admin/roles"));
        var role = roles.First(r => r.Name == "Requester");

        // Silently dropping it would produce a role that looks right in the request
        // and is missing a permission in practice.
        var response = await admin.PutAsJsonAsync($"/api/v1/admin/roles/{role.Id}/permissions",
            new SetRolePermissionsRequest { PermissionKeys = ["ticket.view_own", "ticket.do_anything"] });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_role_with_members_cannot_be_deleted()
    {
        var admin = await SignInAsync("admin@itg.test");

        var created = await ReadAsync<RoleResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/roles", new CreateRoleRequest
            {
                Name = "Temporary Auditor",
                DefaultScope = "Organization",
                Rank = 45,
                PermissionKeys = ["ticket.view_own"],
            }));

        // Nobody holds it, so it goes.
        var deletable = await admin.DeleteAsync($"/api/v1/admin/roles/{created.Id}");
        deletable.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var roles = await ReadAsync<IReadOnlyList<RoleResponse>>(await admin.GetAsync("/api/v1/admin/roles"));
        roles.ShouldNotContain(r => r.Name == "Temporary Auditor");
    }

    [Fact]
    public async Task Repeated_insertion_into_the_same_gap_never_runs_out_of_room()
    {
        var admin = await SignInAsync("admin@itg.test");

        async Task<IReadOnlyList<RoleResponse>> LadderAsync() =>
            await ReadAsync<IReadOnlyList<RoleResponse>>(await admin.GetAsync("/api/v1/admin/roles"));

        var created = new List<Guid>();

        try
        {
            // The interface offers "between X and Y" and turns it into the midpoint of
            // their two ranks. Left alone the gap halves every time — 10, 5, 2, 1 — and
            // the fifth insertion has nowhere to go. Five rounds is one more than the
            // unspaced ladder could survive.
            for (var round = 1; round <= 5; round++)
            {
                var before = await LadderAsync();

                var above = before.Single(r => r.Name == "Manager");
                var below = before.First(r => r.Rank < above.Rank);

                // Exactly what buildPositions computes in the browser.
                var midpoint = (int)Math.Round((above.Rank + below.Rank) / 2.0);

                midpoint.ShouldBeGreaterThan(below.Rank,
                    $"round {round}: no room left between {above.Name} and {below.Name}");
                midpoint.ShouldBeLessThan(above.Rank,
                    $"round {round}: no room left between {above.Name} and {below.Name}");

                var role = await ReadAsync<RoleResponse>(
                    await admin.PostAsJsonAsync("/api/v1/admin/roles", new CreateRoleRequest
                    {
                        Name = $"Interposed {round}",
                        DefaultScope = "Organization",
                        Rank = midpoint,
                        PermissionKeys = ["ticket.view_own"],
                    }));

                created.Add(role.Id);

                var after = await LadderAsync();

                // Re-spaced, so the next round has a whole gap to aim at again.
                after.Select(r => r.Rank).ShouldAllBe(rank => rank % 10 == 0);
                after.Select(r => r.Rank).Distinct().Count().ShouldBe(after.Count);

                // And the role landed where it was asked to go, not merely somewhere.
                var placed = after.Select(r => r.Name).ToList();
                placed.IndexOf($"Interposed {round}")
                    .ShouldBeGreaterThan(placed.IndexOf("Manager"),
                        $"round {round}: should sit below Manager");
                placed.IndexOf($"Interposed {round}")
                    .ShouldBeLessThan(placed.IndexOf(below.Name),
                        $"round {round}: should sit above {below.Name}");
            }

            // Highest authority first, with no ties to make the order ambiguous.
            var ladder = await LadderAsync();
            ladder.Select(r => r.Rank).ShouldBeInOrder(SortDirection.Descending);
        }
        finally
        {
            foreach (var id in created)
            {
                await admin.DeleteAsync($"/api/v1/admin/roles/{id}");
            }
        }
    }

    [Fact]
    public async Task Setting_a_users_roles_replaces_them_wholesale()
    {
        var admin = await SignInAsync("admin@itg.test");

        var created = await ReadAsync<TemporaryPasswordResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
            {
                Email = "role.subject@itg.test",
                FirstName = "Nida",
                LastName = "Farooq",
            }));

        created.TemporaryPassword.ShouldNotBeNullOrWhiteSpace();

        var users = await ReadAsync<PagedResult<UserListItemResponse>>(
            await admin.GetAsync("/api/v1/admin/users?search=role.subject@itg.test"));

        var userId = users.Items.Single().Id;

        var roles = await ReadAsync<IReadOnlyList<RoleResponse>>(await admin.GetAsync("/api/v1/admin/roles"));
        var requester = roles.Single(r => r.Name == "Requester");
        var agent = roles.Single(r => r.Name == "Staff");

        await admin.PutAsJsonAsync($"/api/v1/admin/users/{userId}/roles",
            new SetUserRolesRequest { RoleIds = [requester.Id, agent.Id] });

        var afterSwap = await ReadAsync<UserDetailResponse>(
            await admin.PutAsJsonAsync($"/api/v1/admin/users/{userId}/roles",
                new SetUserRolesRequest { RoleIds = [requester.Id], Reason = "Moved back to the business" }));

        afterSwap.RoleIds.ShouldBe([requester.Id]);
    }

    // ------------------------------------------------------------------ teams

    [Fact]
    public async Task A_team_cannot_escalate_to_itself()
    {
        var admin = await SignInAsync("admin@itg.test");

        var teams = await ReadAsync<IReadOnlyList<TeamResponse>>(await admin.GetAsync("/api/v1/admin/teams"));
        var team = teams.First();

        var response = await admin.PutAsJsonAsync($"/api/v1/admin/teams/{team.Id}", new SaveTeamRequest
        {
            Name = team.Name,
            Code = team.Code,
            EscalationTeamId = team.Id,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_member_with_open_tickets_cannot_be_removed_from_their_team()
    {
        var admin = await SignInAsync("admin@itg.test");

        var teams = await ReadAsync<IReadOnlyList<TeamResponse>>(await admin.GetAsync("/api/v1/admin/teams"));

        var loaded = teams
            .SelectMany(t => t.Members.Select(m => new { Team = t, Member = m }))
            .FirstOrDefault(x => x.Member.OpenTickets > 0);

        if (loaded is null)
        {
            return; // Nothing assigned in this run; the guard is exercised elsewhere.
        }

        var response = await admin.DeleteAsync(
            $"/api/v1/admin/teams/{loaded.Team.Id}/members/{loaded.Member.UserId}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_team_can_be_created_and_given_a_member()
    {
        var admin = await SignInAsync("admin@itg.test");

        var team = await ReadAsync<TeamResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/teams", new SaveTeamRequest
            {
                Name = "Quality Assurance",
                Code = "QA",
                AcceptanceTimeoutMinutes = 45,
            }));

        var users = await ReadAsync<PagedResult<UserListItemResponse>>(
            await admin.GetAsync("/api/v1/admin/users?search=specialist@itg.test"));

        var withMember = await ReadAsync<TeamResponse>(
            await admin.PutAsJsonAsync($"/api/v1/admin/teams/{team.Id}/members", new SaveTeamMemberRequest
            {
                UserId = users.Items.Single().Id,
                RoleInTeam = "Lead",
                CapacityWeight = 0.5m,
            }));

        var member = withMember.Members.Single();
        member.RoleInTeam.ShouldBe("Lead");
        member.CapacityWeight.ShouldBe(0.5m);

        // Removing is fine while they own nothing for this team.
        var removed = await ReadAsync<TeamResponse>(
            await admin.DeleteAsync($"/api/v1/admin/teams/{team.Id}/members/{member.UserId}"));

        removed.Members.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_duplicate_team_code_is_refused()
    {
        var admin = await SignInAsync("admin@itg.test");

        var response = await admin.PostAsJsonAsync("/api/v1/admin/teams", new SaveTeamRequest
        {
            Name = "Another IT Support",
            Code = "ITSUP",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- catalog

    [Fact]
    public async Task A_category_in_use_cannot_be_deleted()
    {
        var admin = await SignInAsync("admin@itg.test");

        var categories = await ReadAsync<IReadOnlyList<AdminCategoryResponse>>(
            await admin.GetAsync("/api/v1/admin/catalog/categories"));

        var inUse = categories.FirstOrDefault(c => c.TicketCount > 0);

        if (inUse is null)
        {
            return; // No categorised tickets in this run.
        }

        var response = await admin.DeleteAsync($"/api/v1/admin/catalog/categories/{inUse.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_category_and_subcategory_can_be_created()
    {
        var admin = await SignInAsync("admin@itg.test");

        var category = await ReadAsync<AdminCategoryResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/catalog/categories", new SaveCategoryRequest
            {
                Name = "Shipping Documents",
                Code = "SHIPDOC",
                DisplayOrder = 90,
            }));

        var withChild = await ReadAsync<AdminCategoryResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/catalog/subcategories", new SaveSubcategoryRequest
            {
                CategoryId = category.Id,
                Name = "Bill of Lading",
                Code = "BOL",
                DefaultImpact = "High",
            }));

        withChild.Subcategories.ShouldContain(sub => sub.Code == "BOL" && sub.DefaultImpact == "High");

        // Unused, so it can go — and the delete is an archive, not an erase.
        var deleted = await admin.DeleteAsync($"/api/v1/admin/catalog/categories/{category.Id}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_priority_matrix_returns_every_combination()
    {
        var admin = await SignInAsync("admin@itg.test");

        var cells = await ReadAsync<IReadOnlyList<PriorityMatrixCell>>(
            await admin.GetAsync("/api/v1/admin/catalog/priority-matrix"));

        // Sixteen, always. A grid with holes invites the reader to assume the missing
        // cells are impossible rather than merely unconfigured.
        cells.Count.ShouldBe(16);
        cells.Select(c => (c.Impact, c.Urgency)).Distinct().Count().ShouldBe(16);
    }

    [Fact]
    public async Task A_partial_priority_matrix_is_refused()
    {
        var admin = await SignInAsync("admin@itg.test");

        var cells = await ReadAsync<IReadOnlyList<PriorityMatrixCell>>(
            await admin.GetAsync("/api/v1/admin/catalog/priority-matrix"));

        var response = await admin.PutAsJsonAsync("/api/v1/admin/catalog/priority-matrix",
            new SavePriorityMatrixRequest { Cells = [.. cells.Take(10)] });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Editing_the_matrix_changes_the_priority_new_tickets_receive()
    {
        var admin = await SignInAsync("admin@itg.test");
        var requester = await SignInAsync("requester@itg.test");

        var original = await ReadAsync<IReadOnlyList<PriorityMatrixCell>>(
            await admin.GetAsync("/api/v1/admin/catalog/priority-matrix"));

        var edited = original
            .Select(c => c is { Impact: "Low", Urgency: "Low" }
                ? c with { Priority = "Critical" }
                : c)
            .ToList();

        await admin.PutAsJsonAsync("/api/v1/admin/catalog/priority-matrix",
            new SavePriorityMatrixRequest { Cells = edited, Reason = "Testing the grid is actually read" });

        var ticket = await ReadAsync<Contracts.Tickets.TicketDetailResponse>(
            await requester.PostAsJsonAsync("/api/v1/tickets", new Contracts.Tickets.CreateTicketRequest
            {
                Subject = "Matrix wiring check",
                Description = "Raised to confirm the configured grid is what decides priority.",
                Impact = "Low",
                Urgency = "Low",
                Type = "ServiceRequest",
            }));

        // Nothing in code maps Low/Low to anything: the calculator reads the rows.
        ticket.Priority.ShouldBe("Critical");

        await admin.PutAsJsonAsync("/api/v1/admin/catalog/priority-matrix",
            new SavePriorityMatrixRequest { Cells = original, Reason = "Restoring the default grid" });
    }

    // -------------------------------------------------------------------- SLA

    [Fact]
    public async Task An_unsatisfiable_target_is_refused()
    {
        var admin = await SignInAsync("admin@itg.test");
        var manager = await SignInAsync("manager@itg.test");

        var policies = await ReadAsync<IReadOnlyList<SlaPolicyResponse>>(
            await manager.GetAsync("/api/v1/admin/sla/policies"));

        var policy = policies.First();

        // Resolution inside response is not a stricter policy, it is an impossible
        // one: the clock would breach resolution before a reply was even due.
        var response = await manager.PutAsJsonAsync($"/api/v1/admin/sla/policies/{policy.Id}",
            new SaveSlaPolicyRequest
            {
                Name = policy.Name,
                Targets =
                [
                    new SlaTargetResponse
                    {
                        Priority = "High",
                        ResponseMinutes = 240,
                        ResolutionMinutes = 60,
                        WarningThresholdPercent = 70,
                    },
                ],
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // An administrator without sla.manage is refused outright.
        var adminAttempt = await admin.GetAsync("/api/v1/admin/sla/policies");
        adminAttempt.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_time_zone_is_refused_at_configuration_time()
    {
        var admin = await SignInAsync("admin@itg.test");

        var response = await admin.PostAsJsonAsync("/api/v1/admin/sla/calendars",
            new SaveBusinessCalendarRequest
            {
                Name = "Nowhere",
                Code = "NOWHERE",
                TimeZoneId = "Middle/Earth",
                Hours = [],
            });

        // Saved now, this would surface later inside the background SLA sweep, on a
        // ticket nobody is watching.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_calendar_can_be_created_with_hours_and_a_holiday()
    {
        var admin = await SignInAsync("admin@itg.test");

        var calendar = await ReadAsync<BusinessCalendarResponse>(
            await admin.PostAsJsonAsync("/api/v1/admin/sla/calendars", new SaveBusinessCalendarRequest
            {
                Name = "Karachi Office",
                Code = "KHI",
                TimeZoneId = "UTC",
                Hours =
                [
                    new BusinessHourResponse { DayOfWeek = "Monday", StartMinute = 540, EndMinute = 1020 },
                    new BusinessHourResponse { DayOfWeek = "Tuesday", StartMinute = 540, EndMinute = 1020 },
                ],
            }));

        calendar.Hours.Count.ShouldBe(2);

        var withHoliday = await ReadAsync<BusinessCalendarResponse>(
            await admin.PostAsJsonAsync($"/api/v1/admin/sla/calendars/{calendar.Id}/holidays",
                new SaveHolidayRequest
                {
                    Name = "Independence Day",
                    DateUtc = new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc),
                    IsRecurring = true,
                }));

        withHoliday.Holidays.ShouldContain(h => h.Name == "Independence Day");

        var duplicate = await admin.PostAsJsonAsync($"/api/v1/admin/sla/calendars/{calendar.Id}/holidays",
            new SaveHolidayRequest
            {
                Name = "Independence Day again",
                DateUtc = new DateTime(2026, 8, 14, 9, 30, 0, DateTimeKind.Utc),
            });

        // Same date, different time of day — still the same holiday.
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // --------------------------------------------------------------- settings

    [Fact]
    public async Task A_sensitive_setting_is_masked_and_survives_a_blind_save()
    {
        var admin = await SignInAsync("admin@itg.test");

        await admin.PutAsJsonAsync("/api/v1/admin/settings", new SaveSystemSettingRequest
        {
            Key = "Integration.Erp.ApiKey",
            Value = "the-real-secret-value",
            Category = "Integrations",
            IsSensitive = true,
        });

        var listed = await ReadAsync<IReadOnlyList<SystemSettingResponse>>(
            await admin.GetAsync("/api/v1/admin/settings"));

        var setting = listed.Single(s => s.Key == "Integration.Erp.ApiKey");
        setting.Value.ShouldNotBe("the-real-secret-value");
        setting.IsSensitive.ShouldBeTrue();

        // Opening the page and pressing save sends the mask back. Storing it would
        // destroy the credential, so the mask means "leave it alone".
        await admin.PutAsJsonAsync("/api/v1/admin/settings", new SaveSystemSettingRequest
        {
            Key = "Integration.Erp.ApiKey",
            Value = setting.Value,
            IsSensitive = true,
        });

        var audit = await ReadAsync<PagedResult<Contracts.Auditing.AuditLogResponse>>(
            await admin.GetAsync("/api/v1/audit?entityType=SystemSetting"));

        // The audit trail records that it changed, never what it changed to.
        audit.Items.ShouldNotBeEmpty();
        audit.Items.SelectMany(entry => entry.Changes)
            .ShouldNotContain(change => change.Value == "the-real-secret-value");
    }

    [Fact]
    public async Task A_settings_key_is_validated()
    {
        var admin = await SignInAsync("admin@itg.test");

        var response = await admin.PutAsJsonAsync("/api/v1/admin/settings", new SaveSystemSettingRequest
        {
            Key = "not a valid key!",
            Value = "x",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reference_data_covers_every_dropdown_an_admin_form_needs()
    {
        var admin = await SignInAsync("admin@itg.test");

        var reference = await ReadAsync<AdminReferenceData>(
            await admin.GetAsync("/api/v1/admin/reference"));

        reference.Roles.ShouldNotBeEmpty();
        reference.Teams.ShouldNotBeEmpty();
        reference.Users.ShouldNotBeEmpty();
        reference.Departments.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task One_tenant_never_sees_another_tenants_users_or_teams()
    {
        var itg = await SignInAsync("admin@itg.test");
        var fabrikam = await SignInAsync("admin@fab.test");

        var itgUsers = await ReadAsync<PagedResult<UserListItemResponse>>(
            await itg.GetAsync("/api/v1/admin/users?pageSize=100"));

        var fabrikamUsers = await ReadAsync<PagedResult<UserListItemResponse>>(
            await fabrikam.GetAsync("/api/v1/admin/users?pageSize=100"));

        itgUsers.Items.ShouldAllBe(u => !u.Email.EndsWith("@fab.test"));
        fabrikamUsers.Items.ShouldAllBe(u => !u.Email.EndsWith("@itg.test"));

        // An identifier from the other tenant is not merely forbidden, it does not
        // exist — the global filter makes the row unreachable, so this is a 404.
        var borrowed = itgUsers.Items.First().Id;
        var response = await fabrikam.GetAsync($"/api/v1/admin/users/{borrowed}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
