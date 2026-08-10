using Charter.Notifications;

namespace Charter.Tests;

/// <summary>
/// Covers change spec 001 part C.3 - both renderings on every template - and the copy rules that
/// make an email safe to send to a non-engineer (sections 7.1, 11).
/// </summary>
public class EmailTemplateTests
{
    private static readonly Uri Thread = new("https://charter.example.com/requests/1234");

    public static TheoryData<EmailContent> EveryTemplate() =>
    [
        EmailTemplates.Invitation(new InvitationEmail
        {
            InviterName = "Priya",
            OrganizationName = "Acme",
            AcceptUrl = new Uri("https://charter.example.com/invite/token"),
            ExpiresAt = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero),
        }),
        EmailTemplates.PasswordReset(new PasswordResetEmail
        {
            RecipientName = "Sam",
            ResetUrl = new Uri("https://charter.example.com/reset/token"),
            ValidFor = TimeSpan.FromHours(1),
        }),
        EmailTemplates.QuestionForYou(new QuestionForYouEmail
        {
            RecipientName = "Sam",
            RequestSummary = "Let customers download their invoices",
            Question = "Should the download include invoices that have already been paid?",
            ThreadUrl = Thread,
        }),
        EmailTemplates.ReadyToTry(new ReadyToTryEmail
        {
            RecipientName = "Sam",
            RequestSummary = "Let customers download their invoices",
            WhatToCheck = ["Open an old invoice and download it", "Check the total matches"],
            ThreadUrl = Thread,
        }),
        EmailTemplates.Test("charter.example.com", new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero)),
    ];

    [Theory]
    [MemberData(nameof(EveryTemplate))]
    public void EveryTemplateRendersAsHtmlAndAsPlainText(EmailContent content)
    {
        // C.3: templates in both HTML and plain text. The plain-text body is the one that reaches a
        // screen reader, a watch, and a client with HTML turned off.
        Assert.False(string.IsNullOrWhiteSpace(content.Subject));
        Assert.False(string.IsNullOrWhiteSpace(content.Text));
        Assert.False(string.IsNullOrWhiteSpace(content.Html));

        Assert.DoesNotContain('<', content.Text);
        Assert.Contains("<p", content.Html, StringComparison.Ordinal);

        // Section 6: never show an ETA. Elapsed time only, and nothing that reads as a promise.
        foreach (var body in new[] { content.Text, content.Html, content.Subject })
        {
            Assert.DoesNotContain("estimated", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("should be ready", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(EveryTemplate))]
    public void EveryTemplateIsSelfContained(EmailContent content)
    {
        // Mail clients strip external stylesheets and block remote images. A template that needs
        // either is illegible in Outlook and in every client with images off.
        Assert.DoesNotContain("<img", content.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", content.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", content.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", content.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheInvitationSaysWhoInvitedYouAndWhereToGo()
    {
        var content = EmailTemplates.Invitation(new InvitationEmail
        {
            InviterName = "Priya",
            OrganizationName = "Acme",
            AcceptUrl = new Uri("https://charter.example.com/invite/token"),
        });

        Assert.Equal("Priya invited you to Acme", content.Subject);
        Assert.Contains("https://charter.example.com/invite/token", content.Text, StringComparison.Ordinal);
        Assert.Contains("https://charter.example.com/invite/token", content.Html, StringComparison.Ordinal);

        // Section 7.1: the reader may never have seen a repository. The copy assumes nothing.
        Assert.Contains("plain English", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePasswordResetSaysHowLongTheLinkLastsAndWhatToDoIfItWasNotYou()
    {
        var content = EmailTemplates.PasswordReset(new PasswordResetEmail
        {
            RecipientName = "Sam",
            ResetUrl = new Uri("https://charter.example.com/reset/token"),
            ValidFor = TimeSpan.FromMinutes(30),
        });

        Assert.Contains("30 minutes", content.Text, StringComparison.Ordinal);
        Assert.Contains("If this was not you", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuestionTemplateLeadsWithTheQuestion()
    {
        var content = EmailTemplates.QuestionForYou(new QuestionForYouEmail
        {
            RecipientName = "Sam",
            RequestSummary = "Let customers download their invoices",
            Question = "Should the download include invoices that have already been paid?",
            ThreadUrl = Thread,
        });

        Assert.Equal("A question about your request", content.Subject);
        Assert.Contains("Sam,", content.Text, StringComparison.Ordinal);
        Assert.Contains("already been paid", content.Text, StringComparison.Ordinal);
        Assert.Contains(Thread.ToString(), content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadyToTryTemplateCarriesWhatToCheck()
    {
        // Section 11: "what to check" beside the preview button. Without it a preview URL is a dead
        // end - the recipient does not know what they are looking at or what would count as working.
        var content = EmailTemplates.ReadyToTry(new ReadyToTryEmail
        {
            RequestSummary = "Let customers download their invoices",
            WhatToCheck = ["Open an old invoice and download it", "Check the total matches"],
            ThreadUrl = Thread,
        });

        Assert.Equal("Ready to try", content.Subject);
        Assert.Contains("What to check:", content.Text, StringComparison.Ordinal);
        Assert.Contains("- Open an old invoice and download it", content.Text, StringComparison.Ordinal);
        Assert.Contains("<li", content.Html, StringComparison.Ordinal);

        // Section 11: feedback is two buttons, and "Not quite" has to feel as welcome as "Works".
        Assert.Contains("Not quite", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequesterFacingTemplateCarriesNoRepoNameNoShaAndNoStackTrace()
    {
        // Section 7.1: a requester never sees a repo name, a branch or a diff. Section 11: a
        // non-engineer who sees a stack trace once never files again. The primary control is that
        // the model has nowhere to put any of it; this is the second layer, for the free text it
        // does carry.
        const string debris = """
            The build failed while writing to acme/checkout@a1b2c3d4e5f6a7b8.
            System.NullReferenceException: Object reference not set to an instance of an object.
               at Charter.Runners.AgentSession.WriteAsync(String path) in /src/AgentSession.cs:line 42
               at Charter.Orchestration.SessionCoordinator.RunAsync()
            --- End of stack trace from previous location ---
            See https://github.com/acme/checkout/commit/deadbeefdeadbeefdeadbeefdeadbeefdeadbeef
            """;

        var content = EmailTemplates.QuestionForYou(new QuestionForYouEmail
        {
            RecipientName = "Sam",
            RequestSummary = "Let customers download their invoices",
            Question = debris,
            ThreadUrl = Thread,
        });

        foreach (var body in new[] { content.Text, content.Html, content.Subject })
        {
            Assert.DoesNotContain("acme/checkout", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("a1b2c3d4e5f6a7b8", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("deadbeef", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("github.com", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NullReferenceException", body, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentSession.cs", body, StringComparison.Ordinal);
            Assert.DoesNotContain(":line 42", body, StringComparison.Ordinal);
            Assert.DoesNotContain("at Charter.", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRequesterFacingModelsHaveNowhereToPutEngineerDetail()
    {
        // The structural half of the same rule. A regular expression can be argued with; a type with
        // no such property cannot.
        var forbidden = new[] { "repo", "repository", "branch", "sha", "commit", "diff", "token", "cost" };

        foreach (var type in new[] { typeof(QuestionForYouEmail), typeof(ReadyToTryEmail) })
        {
            foreach (var property in type.GetProperties())
            {
                Assert.DoesNotContain(
                    forbidden,
                    word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void ARequestersOwnWordsAreNotMangled()
    {
        // Their sentence is theirs. Scrubbing a summary because a fragment of it looks like a hash
        // would quote somebody's request back to them wrongly, which is worse than quoting it.
        var content = EmailTemplates.ReadyToTry(new ReadyToTryEmail
        {
            RequestSummary = "Support read/write access for order 1234567 and/or its invoices",
            ThreadUrl = Thread,
        });

        Assert.Contains("read/write", content.Text, StringComparison.Ordinal);
        Assert.Contains("and/or", content.Text, StringComparison.Ordinal);
        Assert.Contains("1234567", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LongFreeTextIsTruncatedRatherThanSentWhole()
    {
        var content = EmailTemplates.QuestionForYou(new QuestionForYouEmail
        {
            RequestSummary = string.Join(' ', Enumerable.Repeat("invoices", 200)),
            Question = "Which ones?",
            ThreadUrl = Thread,
        });

        Assert.True(content.Text.Length < 4000);
        Assert.Contains("…", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlIsEscapedRatherThanInterpolated()
    {
        var content = EmailTemplates.QuestionForYou(new QuestionForYouEmail
        {
            RequestSummary = "Add a <script>alert(1)</script> button",
            Question = "Which page?",
            ThreadUrl = Thread,
        });

        Assert.DoesNotContain("<script>", content.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNotificationFooterSaysWhyTheEmailArrivedAndHowToStopIt()
    {
        // Section 22: per-user channel preference. An email with no way back to that setting is how
        // a product gets filtered rather than reconfigured.
        var content = EmailTemplates.ReadyToTry(new ReadyToTryEmail
        {
            RequestSummary = "Let customers download their invoices",
            ThreadUrl = Thread,
            NotificationSettingsUrl = new Uri("https://charter.example.com/settings/notifications"),
        });

        Assert.Contains("only emails you about the two things", content.Text, StringComparison.Ordinal);
        Assert.Contains("settings/notifications", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubbingLeavesOrdinaryProseAlone()
    {
        const string prose = "Check the totals and/or the read/write permissions on 24/7 support.";

        Assert.Equal(prose, RequesterSafeText.Scrub(prose));
    }

    [Theory]
    [InlineData("Deployed a1b2c3d4e5f6 to preview", "a1b2c3d4e5f6")]
    [InlineData("See acme/checkout@a1b2c3d4e5f6", "acme/checkout")]
    [InlineData("Cloned git@example.com:acme/checkout.git", "checkout.git")]
    public void ScrubbingRemovesTheShapesThatHaveNoInnocentReading(string text, string removed)
        => Assert.DoesNotContain(removed, RequesterSafeText.Scrub(text), StringComparison.OrdinalIgnoreCase);
}
