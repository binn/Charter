using Charter.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data.Configurations;

/// <summary>
/// The refinement conversation and its turns (sections 2.3, 10, 10b).
/// </summary>
/// <remarks>
/// Section 2.3 forbids in-memory orchestration state outright: the container restarts mid-session and
/// every session must be resumable from Postgres alone. A refinement conversation is orchestration
/// state — the mode, the turns, the flags an engineer has yet to clear, and whether a human confirmed
/// the acceptance criteria — so it is a table rather than a field on a service.
/// </remarks>
internal sealed class ConversationConfiguration : IEntityTypeConfiguration<ConversationRecord>
{
    public void Configure(EntityTypeBuilder<ConversationRecord> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(conversation => conversation.Id);

        builder.Property(conversation => conversation.Id).ValueGeneratedNever();
        builder.Property(conversation => conversation.Mode).HasEnumConversion();
        builder.Property(conversation => conversation.StartedAt).IsRequired();
        builder.Property(conversation => conversation.UpdatedAt).IsRequired();

        // The flags themselves are jsonb and the count is a column, so the review gate is a cheap
        // predicate rather than a document parse on every read.
        builder.Property(conversation => conversation.Flags).IsOptionalJsonb();
        builder.Property(conversation => conversation.FlagCount).IsRequired();
        builder.Property(conversation => conversation.FlagsCleared).IsRequired();

        // The structured spec is the single source of truth and both renderings are projections over
        // it (section 10b), so the document is what is stored - never one of the renderings.
        builder.Property(conversation => conversation.Spec).IsOptionalJsonb();
        builder.Property(conversation => conversation.ConfirmedContentHash).HasMaxLength(128);

        builder.Ignore(conversation => conversation.RequiresEngineerReview);
        builder.Ignore(conversation => conversation.IsConfirmed);
        builder.Ignore(conversation => conversation.AllowsRepoWrite);

        builder.HasMany(conversation => conversation.Turns)
            .WithOne()
            .HasForeignKey(turn => turn.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(conversation => conversation.Turns)
            .HasField("_turns")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(conversation => conversation.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // A conversation outlives the request it produced: section 10b promotes one surface upward,
        // and deleting the request must not silently erase the conversation that justified it.
        builder.HasOne<Request>()
            .WithMany()
            .HasForeignKey(conversation => conversation.RequestId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(conversation => conversation.ConfirmedBy)
            .OnDelete(DeleteBehavior.SetNull);

        // Resuming after a restart reads a user's or an organisation's live conversations newest
        // first, so this is the index that makes section 2.3 cheap rather than theoretical.
        builder.HasIndex(conversation => new { conversation.OrgId, conversation.UpdatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_conversations_org_id_updated_at");

        builder.HasIndex(conversation => conversation.RequestId)
            .HasDatabaseName("ix_conversations_request_id");
    }
}

/// <summary>
/// One turn of a conversation.
/// </summary>
/// <remarks>
/// The stored characters map to a private property, so the column exists and no public member hands
/// it out untyped. <c>ConversationTurnRecord</c> exposes model-authored text as a string and requester
/// text only as a <c>RequesterText</c>, which is section 16's boundary carried into persistence
/// rather than around it.
/// </remarks>
internal sealed class ConversationTurnConfiguration : IEntityTypeConfiguration<ConversationTurnRecord>
{
    /// <summary>The private property holding the turn's characters.</summary>
    private const string BodyProperty = "Body";

    public void Configure(EntityTypeBuilder<ConversationTurnRecord> builder)
    {
        builder.ToTable("conversation_turns");
        builder.HasKey(turn => turn.Id);

        builder.Property(turn => turn.Id).ValueGeneratedNever();
        builder.Property(turn => turn.Seq).IsRequired();
        builder.Property(turn => turn.Kind).HasEnumConversion();
        builder.Property(turn => turn.Mode).HasEnumConversion();
        builder.Property(turn => turn.At).IsRequired();

        builder.Property<string>(BodyProperty)
            .HasColumnName("body")
            .IsRequired();

        builder.Ignore(turn => turn.IsUntrusted);
        builder.Ignore(turn => turn.Length);
        builder.Ignore(turn => turn.AuthoredText);
        builder.Ignore(turn => turn.RequesterText);

        // Ordering is the turn's own, so a reload rebuilds the transcript exactly. Unique per
        // conversation, which is what keeps seq monotonic whichever writer produced the row.
        builder.HasIndex(turn => new { turn.ConversationId, turn.Seq })
            .IsUnique()
            .HasDatabaseName("ux_conversation_turns_conversation_id_seq");
    }
}
