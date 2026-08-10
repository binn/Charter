using Charter.Runners.SchemaChanges;

namespace Charter.Tests;

/// <summary>
/// Section 15, classified structurally rather than heuristically.
/// </summary>
/// <remarks>
/// The fixtures below are shaped exactly as EF Core generates them, because the whole point of
/// section 15 is that the classifier reads the <c>Up</c> operations rather than grepping the file for
/// the word DROP. A test built on invented syntax would prove nothing about the real input.
/// </remarks>
public class RunnerMigrationClassificationTests
{
    private static string Migration(string body) => $$"""
        using System;
        using Microsoft.EntityFrameworkCore.Migrations;

        #nullable disable

        namespace Charter.Data.Migrations;

        /// <inheritdoc />
        public partial class Example : Migration
        {
            /// <inheritdoc />
            protected override void Up(MigrationBuilder migrationBuilder)
            {
        {{body}}
            }

            /// <inheritdoc />
            protected override void Down(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.DropTable(name: "everything");
            }
        }
        """;

    [Fact]
    public void ANewTableAnIndexAndANullableColumnAreAdditive()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.CreateTable(
                        name: "quotes",
                        columns: table => new
                        {
                            id = table.Column<Guid>(type: "uuid", nullable: false),
                            vertical = table.Column<string>(type: "text", nullable: true)
                        },
                        constraints: table =>
                        {
                            table.PrimaryKey("pk_quotes", x => x.id);
                        });

                    migrationBuilder.AddColumn<string>(
                        name: "last_vertical",
                        table: "quotes",
                        type: "text",
                        nullable: true);

                    migrationBuilder.CreateIndex(
                        name: "ix_quotes_vertical",
                        table: "quotes",
                        column: "vertical");
            """));

        Assert.Equal(MigrationClass.Additive, classification.Class);
        Assert.Equal(MigrationOutcome.Flows, classification.Outcome);
        Assert.False(classification.HaltsSession);
        Assert.Contains(MigrationClassification.SchemaChangeLabel, classification.Summary, StringComparison.Ordinal);
        Assert.Equal(3, classification.Findings.Count);
    }

    [Fact]
    public void ARenameIsAmbiguousAndBlocksThePullRequest()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.RenameColumn(
                        name: "vertical",
                        table: "quotes",
                        newName: "selected_vertical");
            """));

        Assert.Equal(MigrationClass.Ambiguous, classification.Class);
        Assert.Equal(MigrationOutcome.RequiresReview, classification.Outcome);
        Assert.Contains("blocked", classification.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonNullColumnWithADefaultIsAmbiguous()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.AddColumn<bool>(
                        name: "is_archived",
                        table: "quotes",
                        type: "boolean",
                        nullable: false,
                        defaultValue: false);
            """));

        Assert.Equal(MigrationClass.Ambiguous, classification.Class);
        Assert.Contains("take the default", Assert.Single(classification.Findings).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonNullColumnWithoutADefaultIsDestructiveAndHaltsTheSession()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.AddColumn<string>(
                        name: "owner",
                        table: "quotes",
                        type: "text",
                        nullable: false);
            """));

        Assert.Equal(MigrationClass.Destructive, classification.Class);
        Assert.Equal(MigrationOutcome.HaltsSession, classification.Outcome);
        Assert.True(classification.HaltsSession);
        Assert.Contains("by hand", classification.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DroppingAColumnIsDestructive()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.DropColumn(
                        name: "legacy_vertical",
                        table: "quotes");
            """));

        Assert.Equal(MigrationClass.Destructive, classification.Class);
        Assert.True(classification.HaltsSession);
    }

    [Fact]
    public void TheWorstOperationDecidesTheWholeMigration()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.CreateIndex(name: "ix_a", table: "quotes", column: "a");
                    migrationBuilder.RenameColumn(name: "b", table: "quotes", newName: "c");
                    migrationBuilder.DropTable(name: "old_quotes");
            """));

        Assert.Equal(MigrationClass.Destructive, classification.Class);
        Assert.Equal(3, classification.Findings.Count);
        Assert.Equal("DropTable", Assert.Single(classification.Worst).Operation);
    }

    [Fact]
    public void OnlyTheUpMethodIsInspected()
    {
        // Every generated Down drops something. Classifying on the whole file would make every
        // migration destructive, which is exactly what "inspect the Up operations" rules out.
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.CreateTable(
                        name: "quotes",
                        columns: table => new { id = table.Column<Guid>(nullable: false) },
                        constraints: table => { table.PrimaryKey("pk_quotes", x => x.id); });
            """));

        Assert.Equal(MigrationClass.Additive, classification.Class);
    }

    [Fact]
    public void AStringLiteralMentioningDropIsNotAnOperation()
    {
        // The heuristic this section rules out, tested directly.
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.CreateIndex(
                        name: "ix_quotes_drop_table_note",
                        table: "quotes",
                        column: "note");
            """));

        Assert.Equal(MigrationClass.Additive, classification.Class);
        Assert.Equal("CreateIndex", Assert.Single(classification.Findings).Operation);
    }

    [Fact]
    public void AnUnreadableMigrationHaltsRatherThanBeingTreatedAsEmpty()
    {
        var classification = MigrationClassifier.Classify("this is not a migration at all");

        Assert.Equal(MigrationClass.Destructive, classification.Class);
        Assert.True(classification.HaltsSession);
    }

    [Fact]
    public void AnOperationCharterDoesNotModelIsAmbiguousRatherThanAssumedSafe()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.PartitionTableByMonth(name: "events");
            """));

        Assert.Equal(MigrationClass.Ambiguous, classification.Class);
        Assert.Contains("does not model", Assert.Single(classification.Findings).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RawSqlIsAmbiguousBecauseCharterWillNotGrepIt()
    {
        var classification = MigrationClassifier.Classify(Migration("""
                    migrationBuilder.Sql("UPDATE quotes SET vertical = 'solar' WHERE vertical IS NULL;");
            """));

        Assert.Equal(MigrationClass.Ambiguous, classification.Class);
    }
}

/// <summary>The parser itself: named arguments, literals, comments and nesting.</summary>
public class RunnerMigrationParserTests
{
    [Fact]
    public void NamedArgumentsAreBrokenOutByName()
    {
        var operations = EfMigrationParser.ParseOperations(
            """
            migrationBuilder.AddColumn<string>(
                name: "owner",
                table: "quotes",
                type: "text",
                nullable: false,
                defaultValue: "");
            """,
            "migrationBuilder");

        var operation = Assert.Single(operations);

        Assert.Equal("AddColumn", operation.Name);
        Assert.Equal("string", operation.TypeArgument);
        Assert.Equal("owner", operation.Text("name"));
        Assert.Equal("quotes", operation.Text("table"));
        Assert.False(operation.Nullable);
        Assert.True(operation.HasDefault);
    }

    [Fact]
    public void ACommentedOutOperationIsNotAnOperation()
    {
        var operations = EfMigrationParser.ParseOperations(
            """
            // migrationBuilder.DropTable(name: "quotes");
            /* migrationBuilder.DropColumn(name: "a", table: "quotes"); */
            migrationBuilder.CreateIndex(name: "ix", table: "quotes", column: "a");
            """,
            "migrationBuilder");

        Assert.Equal("CreateIndex", Assert.Single(operations).Name);
    }

    [Fact]
    public void ANestedLambdaDoesNotSwallowTheNextOperation()
    {
        var operations = EfMigrationParser.ParseOperations(
            """
            migrationBuilder.CreateTable(
                name: "quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotes", x => x.id);
                });
            migrationBuilder.DropColumn(name: "old", table: "quotes");
            """,
            "migrationBuilder");

        Assert.Equal(["CreateTable", "DropColumn"], operations.Select(operation => operation.Name));
    }

    [Fact]
    public void AVerbatimStringContainingAParenthesisDoesNotConfuseTheParser()
    {
        var operations = EfMigrationParser.ParseOperations(
            """"
            migrationBuilder.Sql(@"UPDATE quotes SET note = 'a) b (c' WHERE id IS NOT NULL;");
            migrationBuilder.CreateIndex(name: "ix", table: "quotes", column: "a");
            """",
            "migrationBuilder");

        Assert.Equal(["Sql", "CreateIndex"], operations.Select(operation => operation.Name));
    }

    [Fact]
    public void ABuilderNamedSomethingElseIsStillFound()
    {
        Assert.True(EfMigrationParser.TryParseUp(
            """
            public partial class Example : Migration
            {
                protected override void Up(MigrationBuilder mb)
                {
                    mb.DropTable(name: "quotes");
                }
            }
            """,
            out var operations,
            out _));

        Assert.Equal("DropTable", Assert.Single(operations!).Name);
    }
}

/// <summary>Section 15's rules are configurable from <c>.charter/policies/migrations.yml</c>.</summary>
public class RunnerMigrationPolicyTests
{
    [Fact]
    public void ARepositoryCanReclassifyAnOperation()
    {
        var warnings = new List<string>();

        var policy = MigrationPolicy.Parse(
            """
            version: 1
            operations:
              drop_index: destructive
              rename_column: additive
            """,
            warnings);

        Assert.Empty(warnings);
        Assert.Equal(MigrationClass.Destructive, policy.ClassOf("DropIndex"));
        Assert.Equal(MigrationClass.Additive, policy.ClassOf("RenameColumn"));

        // Everything it did not mention keeps the shipped rule.
        Assert.Equal(MigrationClass.Destructive, policy.ClassOf("DropTable"));
    }

    [Fact]
    public void TheTwoColumnShapeRulesAreConfigurableToo()
    {
        var policy = MigrationPolicy.Parse(
            """
            version: 1
            non_null_without_default: ambiguous
            non_null_with_default: additive
            """,
            []);

        var source = """
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                migrationBuilder.AddColumn<string>(name: "owner", table: "quotes", nullable: false);
            }
            """;

        Assert.Equal(MigrationClass.Ambiguous, MigrationClassifier.Classify(source, policy).Class);
        Assert.Equal(MigrationClass.Destructive, MigrationClassifier.Classify(source).Class);
    }

    [Fact]
    public void OperationNamesMatchHoweverTheyAreSpelled()
    {
        var policy = MigrationPolicy.Parse(
            """
            version: 1
            operations:
              DropIndex: destructive
            """,
            []);

        Assert.Equal(MigrationClass.Destructive, policy.ClassOf("drop_index"));
        Assert.Equal(MigrationClass.Destructive, policy.ClassOf("DropIndex"));
    }

    [Fact]
    public void AnUnknownKeyWarnsAndIsIgnored()
    {
        // Section 8: unknown keys warn, never fail, so an old Charter reads a newer file.
        var warnings = new List<string>();

        var policy = MigrationPolicy.Parse(
            """
            version: 1
            some_future_key: whatever
            operations:
              drop_index: destructive
            """,
            warnings);

        Assert.Contains(warnings, warning => warning.Contains("some_future_key", StringComparison.Ordinal));
        Assert.Equal(MigrationClass.Destructive, policy.ClassOf("DropIndex"));
    }

    [Fact]
    public void ATypoedClassKeepsTheStricterDefaultAndSaysSoLoudly()
    {
        var warnings = new List<string>();

        var policy = MigrationPolicy.Parse(
            """
            version: 1
            operations:
              drop_table: addative
            """,
            warnings);

        Assert.Equal(MigrationClass.Destructive, policy.ClassOf("DropTable"));
        Assert.Contains(warnings, warning => warning.Contains("addative", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("operations:\n  drop_table: additive")]
    [InlineData("version: 2\noperations:\n  drop_table: additive")]
    [InlineData("this: [is, not, a, policy")]
    public void APolicyCharterCannotReadDoesNotLoosenAnything(string yaml)
    {
        var warnings = new List<string>();

        var policy = MigrationPolicy.Parse(yaml, warnings);

        Assert.Equal(MigrationClass.Destructive, policy.ClassOf("DropTable"));
        Assert.NotEmpty(warnings);
    }
}
