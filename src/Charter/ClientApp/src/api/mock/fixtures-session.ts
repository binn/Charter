import type {
  ChangedFile,
  EngineerRecap,
  FileDiff,
  Id,
  SessionActions,
  TranscriptEvent,
  TranscriptEventKind,
} from '@/api/types';

/**
 * Engineer-side fixtures: the pane 2 event stream, the pane 3 diffs, and the §14 recap.
 *
 * Kept apart from `fixtures.ts` for one reason beyond size — everything in this file is data the
 * API **omits** for a requester (§7.4). Having it in its own module makes that easy to check: if a
 * requester payload ever gained a field from here, the import would be visible.
 *
 * The stream is generated rather than typed out because §12 requires pane 2 to "stay smooth at tens
 * of thousands of events", and a fixture of twenty proves nothing about that. It is deterministic —
 * a seeded generator, not `Math.random` — so a reload shows the same session and a test can assert
 * against a known event.
 */

const MINUTE = 60_000;

/** The hero session. Everything below is scoped to it; other requests get a short stream or none. */
const HERO_REQUEST: Id = 'req-vertical';

/** Enough events that virtualization is doing real work rather than decorating a short list. */
const HERO_EVENT_COUNT = 12_480;

/**
 * Where pane 1's milestones land in the stream (§12 linkage).
 *
 * These are deliberately spread across the whole session rather than clustered at the start: a
 * milestone pointing at event 11,910 of 12,480 is the case that proves the jump works, because
 * paging backwards to reach it is not a user experience. `fixtures.ts` sets each milestone's
 * `eventSeq` from this table.
 */
export const HERO_MILESTONE_SEQS = {
  understanding: 12,
  changing: 3_140,
  checking: 8_602,
  assembling: 11_910,
} as const;

interface Segment {
  milestoneId: string;
  fromSeq: number;
  toSeq: number;
  verbs: string[];
  targets: string[];
}

const SEGMENTS: Segment[] = [
  {
    milestoneId: 'ms2',
    fromSeq: 1,
    toSeq: HERO_MILESTONE_SEQS.changing - 1,
    verbs: ['read', 'grep', 'glob', 'outline'],
    targets: [
      'src/Northbeam.Quotes/Features/Quotes/QuoteWizard.razor',
      'src/Northbeam.Quotes/Features/Quotes/NewQuoteModel.cs',
      'src/Northbeam.Quotes/Data/QuotesDbContext.cs',
      'src/Northbeam.Quotes/Features/Auth/CurrentUserAccessor.cs',
      'src/Northbeam.Quotes/Data/Quote.cs',
      'docs/quoting.md',
    ],
  },
  {
    milestoneId: 'ms3',
    fromSeq: HERO_MILESTONE_SEQS.changing,
    toSeq: HERO_MILESTONE_SEQS.checking - 1,
    verbs: ['edit', 'read', 'grep', 'build'],
    targets: [
      'src/Northbeam.Quotes/Data/UserQuotePreference.cs',
      'src/Northbeam.Quotes/Features/Quotes/NewQuoteModel.cs',
      'src/Northbeam.Quotes/Features/Auth/CurrentUserAccessor.cs',
      'src/Northbeam.Quotes/Features/Quotes/QuoteWizard.razor',
    ],
  },
  {
    milestoneId: 'ms4',
    fromSeq: HERO_MILESTONE_SEQS.checking,
    toSeq: HERO_MILESTONE_SEQS.assembling - 1,
    verbs: ['test', 'build', 'read'],
    targets: [
      'tests/Northbeam.Quotes.Tests/Quotes/NewQuoteDefaultsTests.cs',
      'tests/Northbeam.Quotes.Tests/Data/MigrationTests.cs',
    ],
  },
  {
    milestoneId: 'ms5',
    fromSeq: HERO_MILESTONE_SEQS.assembling,
    toSeq: HERO_EVENT_COUNT,
    verbs: ['package', 'deploy'],
    targets: ['deploy/preview.yml'],
  },
];

/**
 * The file-write events, at fixed positions so a test can click a known one and assert pane 3
 * opened at the right hunk. `type` is the adapter's own event name (§12b) and is shown verbatim;
 * `kind` is the adapter-independent projection the client actually switches on.
 */
const FILE_WRITES: { seq: number; path: string; hunkIndex: number; summary: string }[] = [
  {
    seq: 3_210,
    path: 'src/Northbeam.Quotes/Data/Migrations/20260607142211_AddUserQuotePreference.cs',
    hunkIndex: 0,
    summary: 'Created the migration adding UserQuotePreference',
  },
  {
    seq: 3_486,
    path: 'src/Northbeam.Quotes/Data/UserQuotePreference.cs',
    hunkIndex: 0,
    summary: 'Added the UserQuotePreference entity',
  },
  {
    seq: 4_102,
    path: 'src/Northbeam.Quotes/Features/Auth/CurrentUserAccessor.cs',
    hunkIndex: 1,
    summary: 'Exposed the current user id to the quote wizard',
  },
  {
    seq: 5_330,
    path: 'src/Northbeam.Quotes/Features/Quotes/NewQuoteModel.cs',
    hunkIndex: 0,
    summary: 'Seeded a new quote from the stored preference',
  },
  {
    seq: 6_015,
    path: 'src/Northbeam.Quotes/Features/Quotes/QuoteWizard.razor',
    hunkIndex: 0,
    summary: 'Pre-selected the vertical on the first step',
  },
  {
    seq: 7_740,
    path: 'tests/Northbeam.Quotes.Tests/Quotes/NewQuoteDefaultsTests.cs',
    hunkIndex: 0,
    summary: 'Added three tests for the agreed cases',
  },
];

/** A seeded LCG. Deterministic across reloads, so the fixture is a fixture and not a lottery. */
function seeded(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state * 1_664_525 + 1_013_904_223) >>> 0;
    return state / 0x1_0000_0000;
  };
}

const KIND_BY_VERB: Record<string, TranscriptEventKind> = {
  read: 'tool_use',
  grep: 'tool_use',
  glob: 'tool_use',
  outline: 'tool_use',
  edit: 'tool_use',
  build: 'command',
  test: 'command',
  package: 'command',
  deploy: 'command',
};

function buildHeroTranscript(now: number): TranscriptEvent[] {
  const random = seeded(0x5eed_1234);
  const writes = new Map(FILE_WRITES.map((write) => [write.seq, write]));
  const startedAt = now - 2 * 60 * MINUTE - 38 * MINUTE;
  const spanMs = 24 * MINUTE;

  const events: TranscriptEvent[] = [];

  for (let seq = 1; seq <= HERO_EVENT_COUNT; seq += 1) {
    const segment =
      SEGMENTS.find((candidate) => seq >= candidate.fromSeq && seq <= candidate.toSeq) ??
      SEGMENTS[SEGMENTS.length - 1]!;

    const createdAt = new Date(
      startedAt + Math.round((seq / HERO_EVENT_COUNT) * spanMs),
    ).toISOString();

    const write = writes.get(seq);
    if (write) {
      events.push({
        seq,
        kind: 'file_write',
        type: 'tool_execution_start',
        summary: write.summary,
        createdAt,
        path: write.path,
        hunkIndex: write.hunkIndex,
        milestoneId: segment.milestoneId,
        level: 'info',
      });
      continue;
    }

    // A handful of warnings and one error, so pane 2 has something to distinguish that is not
    // colour — every level renders an icon and a word as well (§27.7's rule, generalised).
    const roll = random();
    if (seq === 8_940) {
      events.push({
        seq,
        kind: 'diagnostic',
        type: 'stderr',
        summary:
          'NewQuoteDefaultsTests.FirstEverQuoteStartsOnSolar failed: expected Solar, got Battery',
        createdAt,
        milestoneId: segment.milestoneId,
        level: 'error',
      });
      continue;
    }
    if (roll < 0.004) {
      events.push({
        seq,
        kind: 'diagnostic',
        type: 'stderr',
        summary: 'warning CS8618: non-nullable property must contain a non-null value on exit',
        createdAt,
        milestoneId: segment.milestoneId,
        level: 'warning',
      });
      continue;
    }
    if (roll < 0.02) {
      events.push({
        seq,
        kind: 'message',
        type: 'message_end',
        summary:
          'The wizard reads the vertical off the quote row, so there is nowhere to keep a preference yet.',
        createdAt,
        milestoneId: segment.milestoneId,
        level: 'info',
      });
      continue;
    }

    const verb = segment.verbs[Math.floor(roll * segment.verbs.length * 50) % segment.verbs.length]!;
    const target = segment.targets[Math.floor(random() * segment.targets.length)]!;

    events.push({
      seq,
      kind: KIND_BY_VERB[verb] ?? 'tool_use',
      type: verb === 'build' || verb === 'test' ? 'command_end' : 'tool_execution_start',
      summary: `${verb} ${target}`,
      createdAt,
      milestoneId: segment.milestoneId,
      level: 'info',
    });
  }

  return events;
}

let heroCache: { now: number; events: TranscriptEvent[] } | null = null;

/**
 * The full stream for a request, ascending by `seq`. Built once and memoised — twelve thousand
 * objects is cheap to hold and not cheap to rebuild on every page of a scroll.
 */
export function transcriptFor(requestId: Id, now: number): TranscriptEvent[] {
  if (requestId !== HERO_REQUEST) {
    return [];
  }
  if (!heroCache || heroCache.now !== now) {
    heroCache = { now, events: buildHeroTranscript(now) };
  }
  return heroCache.events;
}

/* -------------------------------------------------------------------------- */
/* Pane 3 — the changed files and their diffs                                 */
/* -------------------------------------------------------------------------- */

/**
 * **Risk-ranked, not alphabetical** (§14). Auth and the migration float to the top; the Razor
 * markup and the tests sink. `riskReasons` is what makes the ranking reviewable rather than a
 * coloured word — an unexplained "high" teaches nobody where to start.
 */
export const HERO_CHANGED_FILES: ChangedFile[] = [
  {
    path: 'src/Northbeam.Quotes/Features/Auth/CurrentUserAccessor.cs',
    additions: 9,
    deletions: 2,
    risk: 'high',
    riskReasons: ['Resolves the signed-in user', 'Result is now cached per request'],
  },
  {
    path: 'src/Northbeam.Quotes/Data/Migrations/20260607142211_AddUserQuotePreference.cs',
    additions: 34,
    deletions: 0,
    risk: 'high',
    riskReasons: ['Database migration', 'Additive: new table, no column dropped (§15)'],
  },
  {
    path: 'src/Northbeam.Quotes/Data/UserQuotePreference.cs',
    additions: 22,
    deletions: 0,
    risk: 'medium',
    riskReasons: ['New persisted entity'],
  },
  {
    path: 'src/Northbeam.Quotes/Features/Quotes/NewQuoteModel.cs',
    additions: 18,
    deletions: 4,
    risk: 'medium',
    riskReasons: ['Changes what a new quote starts with'],
  },
  {
    path: 'src/Northbeam.Quotes/Features/Quotes/QuoteWizard.razor',
    additions: 6,
    deletions: 3,
    risk: 'low',
    riskReasons: ['Presentation only'],
  },
  {
    path: 'tests/Northbeam.Quotes.Tests/Quotes/NewQuoteDefaultsTests.cs',
    additions: 47,
    deletions: 0,
    risk: 'low',
    riskReasons: ['Tests'],
  },
];

const DIFFS: Record<string, { language: string; original: string; modified: string }> = {
  'src/Northbeam.Quotes/Features/Auth/CurrentUserAccessor.cs': {
    language: 'csharp',
    original: `namespace Northbeam.Quotes.Features.Auth;

public sealed class CurrentUserAccessor(IHttpContextAccessor accessor) : ICurrentUserAccessor
{
    public string? UserId =>
        accessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public bool IsAuthenticated =>
        accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
`,
    modified: `namespace Northbeam.Quotes.Features.Auth;

public sealed class CurrentUserAccessor(IHttpContextAccessor accessor) : ICurrentUserAccessor
{
    private string? cached;
    private bool resolved;

    public string? UserId
    {
        get
        {
            if (resolved)
            {
                return cached;
            }

            cached = accessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            resolved = true;
            return cached;
        }
    }

    public bool IsAuthenticated =>
        accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
`,
  },
  'src/Northbeam.Quotes/Data/Migrations/20260607142211_AddUserQuotePreference.cs': {
    language: 'csharp',
    original: '',
    modified: `using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Northbeam.Quotes.Data.Migrations;

/// <inheritdoc />
public partial class AddUserQuotePreference : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserQuotePreferences",
            columns: table => new
            {
                UserId = table.Column<string>(maxLength: 128, nullable: false),
                LastVertical = table.Column<string>(maxLength: 32, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserQuotePreferences", x => x.UserId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserQuotePreferences");
    }
}
`,
  },
  'src/Northbeam.Quotes/Data/UserQuotePreference.cs': {
    language: 'csharp',
    original: '',
    modified: `namespace Northbeam.Quotes.Data;

/// <summary>
/// One row per person. Holds the vertical they last chose, so a new quote can start there.
/// </summary>
public sealed class UserQuotePreference
{
    public required string UserId { get; init; }

    public required Vertical LastVertical { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
`,
  },
  'src/Northbeam.Quotes/Features/Quotes/NewQuoteModel.cs': {
    language: 'csharp',
    original: `namespace Northbeam.Quotes.Features.Quotes;

public sealed class NewQuoteModel(QuotesDbContext db)
{
    public Vertical Vertical { get; set; } = Vertical.Solar;

    public async Task<Quote> CreateAsync(CancellationToken token)
    {
        var quote = new Quote { Vertical = Vertical, CreatedAt = DateTimeOffset.UtcNow };
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(token);
        return quote;
    }
}
`,
    modified: `namespace Northbeam.Quotes.Features.Quotes;

public sealed class NewQuoteModel(QuotesDbContext db, ICurrentUserAccessor user)
{
    public Vertical Vertical { get; set; } = Vertical.Solar;

    /// <summary>
    /// Starts the wizard on whatever this person chose last time. A person who has never made a
    /// quote still starts on Solar, which is the third acceptance criterion.
    /// </summary>
    public async Task LoadDefaultsAsync(CancellationToken token)
    {
        if (user.UserId is not { } userId)
        {
            return;
        }

        var preference = await db.UserQuotePreferences
            .SingleOrDefaultAsync(p => p.UserId == userId, token);

        Vertical = preference?.LastVertical ?? Vertical.Solar;
    }

    public async Task<Quote> CreateAsync(CancellationToken token)
    {
        var quote = new Quote { Vertical = Vertical, CreatedAt = DateTimeOffset.UtcNow };
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(token);
        return quote;
    }
}
`,
  },
  'src/Northbeam.Quotes/Features/Quotes/QuoteWizard.razor': {
    language: 'razor',
    original: `@page "/quotes/new"

<h1>New quote</h1>

<VerticalPicker Value="@Model.Vertical" ValueChanged="OnVerticalChanged" />

@code {
    protected override Task OnInitializedAsync() => Task.CompletedTask;
}
`,
    modified: `@page "/quotes/new"

<h1>New quote</h1>

<VerticalPicker Value="@Model.Vertical" ValueChanged="OnVerticalChanged" />

@code {
    protected override async Task OnInitializedAsync()
    {
        await Model.LoadDefaultsAsync(CancellationToken.None);
    }
}
`,
  },
  'tests/Northbeam.Quotes.Tests/Quotes/NewQuoteDefaultsTests.cs': {
    language: 'csharp',
    original: '',
    modified: `namespace Northbeam.Quotes.Tests.Quotes;

public sealed class NewQuoteDefaultsTests
{
    [Fact]
    public async Task NewQuoteStartsOnThePreviousVertical()
    {
        await using var db = Db.InMemory();
        db.UserQuotePreferences.Add(new UserQuotePreference
        {
            UserId = "user-1",
            LastVertical = Vertical.Battery
        });
        await db.SaveChangesAsync();

        var model = new NewQuoteModel(db, User("user-1"));
        await model.LoadDefaultsAsync(default);

        Assert.Equal(Vertical.Battery, model.Vertical);
    }

    [Fact]
    public async Task FirstEverQuoteStartsOnSolar()
    {
        await using var db = Db.InMemory();

        var model = new NewQuoteModel(db, User("brand-new"));
        await model.LoadDefaultsAsync(default);

        Assert.Equal(Vertical.Solar, model.Vertical);
    }

    [Fact]
    public async Task OnePersonsChoiceDoesNotChangeAnothers()
    {
        await using var db = Db.InMemory();
        db.UserQuotePreferences.Add(new UserQuotePreference
        {
            UserId = "user-1",
            LastVertical = Vertical.EvCharging
        });
        await db.SaveChangesAsync();

        var model = new NewQuoteModel(db, User("user-2"));
        await model.LoadDefaultsAsync(default);

        Assert.Equal(Vertical.Solar, model.Vertical);
    }
}
`,
  },
};

/**
 * Hunks are computed here rather than hand-written so they cannot drift from the text above. A real
 * server would emit whatever its diff tool produced; the shape is what matters to pane 3.
 */
function hunksFor(original: string, modified: string) {
  if (original === '') {
    return [
      {
        id: 'hunk-0',
        header: `@@ -0,0 +1,${modified.split('\n').length} @@`,
        originalStartLine: 0,
        modifiedStartLine: 1,
      },
    ];
  }

  const originalLines = original.split('\n');
  const modifiedLines = modified.split('\n');

  let prefix = 0;
  while (
    prefix < originalLines.length &&
    prefix < modifiedLines.length &&
    originalLines[prefix] === modifiedLines[prefix]
  ) {
    prefix += 1;
  }

  let suffix = 0;
  while (
    suffix < originalLines.length - prefix &&
    suffix < modifiedLines.length - prefix &&
    originalLines[originalLines.length - 1 - suffix] === modifiedLines[modifiedLines.length - 1 - suffix]
  ) {
    suffix += 1;
  }

  const originalCount = originalLines.length - prefix - suffix;
  const modifiedCount = modifiedLines.length - prefix - suffix;

  return [
    {
      id: 'hunk-0',
      header: `@@ -${prefix + 1},${originalCount} +${prefix + 1},${modifiedCount} @@`,
      originalStartLine: prefix + 1,
      modifiedStartLine: prefix + 1,
    },
    // A second hunk so `hunkIndex: 1` on the auth file has somewhere to land.
    {
      id: 'hunk-1',
      header: `@@ -${originalLines.length - suffix},${suffix} +${modifiedLines.length - suffix},${suffix} @@`,
      originalStartLine: Math.max(1, originalLines.length - suffix),
      modifiedStartLine: Math.max(1, modifiedLines.length - suffix),
    },
  ];
}

export function fileDiffFor(path: string): FileDiff {
  const entry = DIFFS[path];
  if (!entry) {
    return {
      path,
      language: 'plaintext',
      originalText: '',
      modifiedText: '',
      hunks: [],
      binary: true,
      truncated: false,
    };
  }

  return {
    path,
    language: entry.language,
    originalText: entry.original,
    modifiedText: entry.modified,
    hunks: hunksFor(entry.original, entry.modified),
    binary: false,
    truncated: false,
  };
}

/* -------------------------------------------------------------------------- */
/* §14 recap and §7.5 post-hoc actions                                        */
/* -------------------------------------------------------------------------- */

export function heroRecap(now: number): EngineerRecap {
  return {
    // This session went through the spend gate, so it is not the §7.5 auto-dispatch case. The
    // `req-derate` recap below is, and leads with it.
    autoDispatched: false,
    summaryMd: [
      'The quote wizard read the vertical straight off the quote row, so there was nowhere to keep a',
      'per-person default. This adds a `UserQuotePreference` table keyed on user id, writes to it when',
      'a quote is created, and reads from it when the wizard opens. The approved spec asked for the',
      'choice to be per-person and for a first-ever quote to start on Solar; both are covered by tests.',
    ].join(' '),
    deviations: [
      {
        id: 'dev-1',
        specSaid: 'The remembered choice is yours alone.',
        agentDid:
          'Keyed the table on the auth user id rather than the employee record, because the wizard only has the former in scope. Two accounts for one person would not share a preference.',
        path: 'src/Northbeam.Quotes/Data/UserQuotePreference.cs',
      },
      {
        id: 'dev-2',
        agentDid:
          'Cached the resolved user id on CurrentUserAccessor. Nothing asked for this; it was added because LoadDefaultsAsync would otherwise re-read the claim on every wizard step.',
        path: 'src/Northbeam.Quotes/Features/Auth/CurrentUserAccessor.cs',
      },
    ],
    files: HERO_CHANGED_FILES,
    couldNotVerify: [
      {
        id: 'nv-1',
        text: 'No test covers what happens when the same person has the wizard open in two tabs and picks a different vertical in each.',
      },
      {
        id: 'nv-2',
        text: 'The migration was applied against an empty preview database only. It has not been run against a copy of production.',
      },
    ],
    reviewOrder: [
      'src/Northbeam.Quotes/Features/Auth/CurrentUserAccessor.cs',
      'src/Northbeam.Quotes/Data/Migrations/20260607142211_AddUserQuotePreference.cs',
      'src/Northbeam.Quotes/Features/Quotes/NewQuoteModel.cs',
    ],
    postedToUrl: 'https://github.com/northbeam/quote-tool/pull/142#issuecomment-1',
    postedToTerm: 'pull request',
    generatedAt: new Date(now - 11 * MINUTE).toISOString(),
  };
}

/** §7.5's other case: nobody vetted the spec, so the recap leads with that and carries it in full. */
export function derateRecap(now: number): EngineerRecap {
  return {
    autoDispatched: true,
    summaryMd: [
      'The derate figure is stored once on the quote and read in eleven places, three of which run',
      'after a quote is sent. Making it follow the panel means deciding what happens to a quote that',
      'has already gone out, and the spec does not say. The session halted rather than guessing.',
    ].join(' '),
    specMd: [
      '**Derate factor should follow the panel, not the project**',
      '',
      'When you change a panel on a quote, the derate factor changes with it instead of keeping the',
      'number someone typed at the start.',
      '',
      '- Swapping the panel on a quote updates the derate factor to match the new panel.',
      '- A derate someone has typed in by hand is kept, and marked as manual.',
      '- Quotes already sent are not recalculated.',
    ].join('\n'),
    deviations: [
      {
        id: 'dev-d1',
        specSaid: 'Quotes already sent are not recalculated.',
        agentDid:
          'Could not establish what "already sent" means in the data — there is no sent timestamp, only a status enum with four values, two of which are used inconsistently.',
        path: 'src/Northbeam.Quotes/Data/Quote.cs',
      },
    ],
    files: [
      {
        path: 'src/Northbeam.Quotes/Features/Pricing/DerateCalculator.cs',
        additions: 41,
        deletions: 18,
        risk: 'high',
        riskReasons: ['Money math', 'Feeds the customer-facing payback figure'],
      },
      {
        path: 'src/Northbeam.Quotes/Data/Quote.cs',
        additions: 3,
        deletions: 1,
        risk: 'medium',
        riskReasons: ['Persisted shape'],
      },
    ],
    couldNotVerify: [
      {
        id: 'nv-d1',
        text: 'The existing pricing tests were already failing on main before this session started.',
      },
    ],
    reviewOrder: ['src/Northbeam.Quotes/Features/Pricing/DerateCalculator.cs'],
    generatedAt: new Date(now - 4 * 24 * 60 * MINUTE + 51 * MINUTE).toISOString(),
  };
}

export function heroSessionActions(): SessionActions {
  return {
    canApprove: true,
    canSteer: true,
    canRevise: true,
    canTakeOver: true,
    branch: 'charter/remember-vertical',
  };
}

export function derateSessionActions(): SessionActions {
  return {
    // A failed session is not something to approve — the affordance is absent rather than disabled.
    canApprove: false,
    canSteer: true,
    canRevise: true,
    canTakeOver: true,
    branch: 'charter/derate-follows-panel',
  };
}
