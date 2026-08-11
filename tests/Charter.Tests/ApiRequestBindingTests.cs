using System.Text.Json;
using Charter.Api;
using Charter.Api.Contracts;
using Charter.Auth;
using Charter.Configuration;
using Charter.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Charter.Tests;

/// <summary>
/// Request bodies bind with the same vocabulary the responses are written in.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CharterApiJson"/> is passed explicitly to <c>Results.Json</c> on the way out, and is
/// invisible on the way <em>in</em>: minimal APIs read a body through the host's
/// <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c>. Until <c>AddCharterApi</c> configured those,
/// every body carrying one of the section 12b wire enums failed to bind and the caller got a bare
/// 400 with no body at all — <c>PATCH /api/me/preferences</c>, <c>POST /api/invitations</c>,
/// <c>POST /api/repos/{id}/access</c> with a role grant, and <c>POST /api/members/{id}/roles</c>.
/// </para>
/// <para>
/// Every test in the suite passed throughout, because a test composes the body object itself and
/// never crosses the wire. So these read the options the host will actually bind with, and parse the
/// bytes the SPA actually sends.
/// </para>
/// </remarks>
public class ApiRequestBindingTests
{
    private static JsonSerializerOptions BindingOptions()
    {
        var config = ConfigTestEnvironment.Valid();

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddCharterConfig(config);
        builder.Services.AddCharterData(config.Database.ConnectionString.Reveal());
        builder.Services.AddCharterAuth();
        builder.Services.AddCharterApi();

        var app = builder.Build();

        return app.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value
            .SerializerOptions;
    }

    [Fact]
    public void ARoleGrantBindsFromTheSpellingTheClientSends()
    {
        var body = JsonSerializer.Deserialize<RepoAccessGrantBody>(
            """{"role":"requester","canRequest":true}""",
            BindingOptions());

        Assert.NotNull(body);
        Assert.Equal(ApiRole.Requester, body.Role);
        Assert.True(body.CanRequest);
    }

    [Fact]
    public void ARoleChangeBindsFromTheSpellingTheClientSends()
    {
        var body = JsonSerializer.Deserialize<SetMemberRoleBody>(
            """{"role":"admin","granted":false}""",
            BindingOptions());

        Assert.NotNull(body);
        Assert.Equal(ApiRole.Admin, body.Role);
        Assert.False(body.Granted);
    }

    [Fact]
    public void EveryPreferenceEnumBindsInItsSnakeCaseSpelling()
    {
        // Section 12 and section 13: the SPA writes `just_the_decisions`, not `JustTheDecisions`,
        // because that is how Charter spells it when it writes the same value back out.
        var body = JsonSerializer.Deserialize<UpdatePreferencesBody>(
            """{"theme":"dark","pane":"developer","teachingLevel":"just_the_decisions"}""",
            BindingOptions());

        Assert.NotNull(body);
        Assert.Equal(ApiThemePreference.Dark, body.Theme);
        Assert.Equal(ApiPanePreference.Developer, body.Pane);
        Assert.Equal(ApiTeachingLevel.JustTheDecisions, body.TeachingLevel);
    }

    [Fact]
    public void AnInvitationsRoleListBinds()
    {
        var body = JsonSerializer.Deserialize<InviteMemberBody>(
            """{"email":"priya@example.test","roles":["engineer","approver"]}""",
            BindingOptions());

        Assert.NotNull(body);
        Assert.Equal([ApiRole.Engineer, ApiRole.Approver], body.Roles);
    }

    [Fact]
    public void AnythingSerialisedWithoutNamingTheApiOptionsStillOmitsRatherThanNulls()
    {
        // Section 7.4's mechanism is omission. An endpoint that forgot to pass
        // `CharterApiJson.Options` should still not write a key whose value is null.
        var json = JsonSerializer.Serialize(
            new RepoAccessGrantResponse { Role = ApiRole.Requester, CanRequest = true },
            BindingOptions());

        Assert.DoesNotContain("memberId", json, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"requester\"", json, StringComparison.Ordinal);
    }
}
