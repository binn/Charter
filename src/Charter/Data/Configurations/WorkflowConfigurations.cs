using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

internal sealed class RequestConfiguration : IEntityTypeConfiguration<Request>
{
    public void Configure(EntityTypeBuilder<Request> builder)
    {
        builder.ToTable("requests");
        builder.HasKey(request => request.Id);

        builder.Property(request => request.Id).ValueGeneratedNever();
        builder.Property(request => request.RawText).IsRequired();
        builder.Property(request => request.TemplateId).HasMaxLength(120);
        builder.Property(request => request.Status).HasEnumConversion();
        builder.Property(request => request.CreatedAt).IsRequired();
        builder.Property(request => request.UpdatedAt).IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(request => request.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Repo>()
            .WithMany()
            .HasForeignKey(request => request.RepoId)
            .OnDelete(DeleteBehavior.Cascade);

        // Requests outlive the person who filed them: deleting a user with history is refused rather
        // than silently orphaning the thread. Section 20 handles deletion as a first-class feature.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.RequesterId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(request => new { request.RepoId, request.Status })
            .HasDatabaseName("ix_requests_repo_id_status");

        builder.HasIndex(request => new { request.RequesterId, request.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_requests_requester_id_created_at");

        builder.HasIndex(request => new { request.OrgId, request.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_requests_org_id_created_at");
    }
}

/// <summary>
/// The two buttons of section 11, as rows.
/// </summary>
/// <remarks>
/// A table rather than a column on <c>requests</c>: one thread carries several sessions (section 11),
/// and "not quite, then not quite, then works" is the history that says how many rounds a request
/// took. The thread renders the latest.
/// </remarks>
internal sealed class RequestFeedbackConfiguration : IEntityTypeConfiguration<RequestFeedback>
{
    public void Configure(EntityTypeBuilder<RequestFeedback> builder)
    {
        builder.ToTable("request_feedback");
        builder.HasKey(feedback => feedback.Id);

        builder.Property(feedback => feedback.Id).ValueGeneratedNever();
        builder.Property(feedback => feedback.Verdict).HasEnumConversion();
        builder.Property(feedback => feedback.Note).HasMaxLength(RequestFeedback.MaxNoteLength);
        builder.Property(feedback => feedback.CreatedAt).IsRequired();

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(feedback => feedback.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        // The session can be pruned (section 20) long after somebody said whether it worked, and the
        // verdict outlives it.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(feedback => feedback.SessionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(feedback => feedback.SubmittedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // The thread reads the latest verdict for one request, which is the only access pattern.
        builder.HasIndex(feedback => new { feedback.RequestId, feedback.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_request_feedback_request_id_created_at");
    }
}

internal sealed class SpecConfiguration : IEntityTypeConfiguration<Spec>
{
    public void Configure(EntityTypeBuilder<Spec> builder)
    {
        builder.ToTable("specs");
        builder.HasKey(spec => spec.Id);

        builder.Property(spec => spec.Id).ValueGeneratedNever();
        builder.Property(spec => spec.Version).IsRequired();
        builder.Property(spec => spec.Title).HasMaxLength(300).IsRequired();
        builder.Property(spec => spec.Outcome).IsRequired();
        builder.Property(spec => spec.BodyMd).IsRequired();

        // Authored in plain language and shared verbatim by both renderings (section 10b). They are
        // the contract the requester approved, and the "what to check" list is rendered from them.
        builder.Property(spec => spec.AcceptanceCriteria).IsJsonb().IsRequired();
        builder.Property(spec => spec.Scope).IsOptionalJsonb();
        builder.Property(spec => spec.Risks).IsOptionalJsonb();
        builder.Property(spec => spec.OpenQuestions).IsOptionalJsonb();
        builder.Property(spec => spec.CreatedAt).IsRequired();

        builder.Ignore(spec => spec.IsApproved);

        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(spec => spec.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(spec => spec.ApprovedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(spec => new { spec.RequestId, spec.Version })
            .IsUnique()
            .HasDatabaseName("ux_specs_request_id_version");
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id).ValueGeneratedNever();
        builder.Property(session => session.Runner).HasEnumConversion();
        builder.Property(session => session.AgentModel).HasMaxLength(200).IsRequired();
        builder.Property(session => session.BaseCommitSha).HasMaxLength(64);
        builder.Property(session => session.Status).HasEnumConversion();
        builder.Property(session => session.AutoDispatched).IsRequired();
        builder.Property(session => session.CostUsd).IsMoney();
        builder.Property(session => session.CreatedAt).IsRequired();

        // Written by the orchestrator, by webhooks, and by the cancel button. See IVersionedEntity.
        builder.Property(session => session.Version).IsConcurrencyToken();

        builder.Ignore(session => session.IsTerminal);

        builder.HasOne<Spec>()
            .WithMany()
            .HasForeignKey(session => session.SpecId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(session => session.SpecId).HasDatabaseName("ix_sessions_spec_id");

        builder.HasIndex(session => new { session.Status, session.CreatedAt })
            .HasDatabaseName("ix_sessions_status_created_at");
    }
}

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");
        builder.HasKey(@event => @event.Id);

        builder.Property(@event => @event.Id).ValueGeneratedNever();
        builder.Property(@event => @event.Seq).IsRequired();
        builder.Property(@event => @event.Type).HasMaxLength(80).IsRequired();
        builder.Property(@event => @event.Payload).IsJsonb().IsRequired();
        builder.Property(@event => @event.CreatedAt).IsRequired();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(@event => @event.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // The whole access pattern in one index: everything reads a session's events in seq order,
        // cursor-paginated on the last seq seen (`seq > @cursor ORDER BY seq LIMIT n`), so cost does
        // not grow with the length of the transcript. Uniqueness makes seq monotonic per session
        // regardless of which writer produced the row.
        builder.HasIndex(@event => new { @event.SessionId, @event.Seq })
            .IsUnique()
            .HasDatabaseName("ux_events_session_id_seq");

        // Retention pruning (section 20) sweeps by age across every session.
        builder.HasIndex(@event => @event.CreatedAt).HasDatabaseName("ix_events_created_at");
    }
}

internal sealed class MilestoneConfiguration : IEntityTypeConfiguration<Milestone>
{
    public void Configure(EntityTypeBuilder<Milestone> builder)
    {
        builder.ToTable("milestones");
        builder.HasKey(milestone => milestone.Id);

        builder.Property(milestone => milestone.Id).ValueGeneratedNever();
        builder.Property(milestone => milestone.Label).HasEnumConversion();
        builder.Property(milestone => milestone.CreatedAt).IsRequired();

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(milestone => milestone.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Pane 1 to pane 2 linkage (section 12): the milestone points at the event that produced it.
        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(milestone => milestone.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(milestone => new { milestone.SessionId, milestone.CreatedAt })
            .HasDatabaseName("ix_milestones_session_id_created_at");

        builder.HasIndex(milestone => milestone.EventId)
            .IsUnique()
            .HasDatabaseName("ux_milestones_event_id");
    }
}
