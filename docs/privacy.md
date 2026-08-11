---
title: "Privacy"
description: "What Charter never collects, the one outbound request it makes and how to turn it off, and where your observability data goes."
---

# Privacy

Charter is self-hosted. Your code stays in your repositories, your data stays in your Postgres, and
Charter never phones home.

This page has three sections: what Charter never collects, the one outbound request it makes and how
to turn it off, and where your observability data goes.

## What Charter never collects

Charter has no usage analytics. There is no opt-in, no opt-out, no consent flow, and no data policy to
read, because nothing is gathered.

Specifically, Charter never sends anywhere:

- Requests, specifications, or refinement conversations
- Agent transcripts, diffs, or any source code
- Repository names, branch names, file paths, or commit SHAs
- User accounts, email addresses, or organisation names
- Instance identifiers, deployment platform, version adoption, or feature usage counts
- Token counts, costs, session outcomes, or error reports

There is no crash reporter and no telemetry endpoint. Nothing is deferred to a "we may collect this in
future" clause.

The accepted tradeoff is that the project gets no aggregate signal about how Charter is used. Problems
surface only when someone opens an issue.

## What ends up in your repository

Separate from the above, and worth stating because it is durable in a way nothing else here is.

When a session produces changes, the runner commits them **authored by the requester** — their display
name and the email address on their Charter account, on both the author and committer fields. That is
deliberate: [spec §7.3](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) requires every
agent action to be attributable to a named human, and a commit is the most durable place that
attribution can live. Charter adds no bot identity and no attribution trailers.

Two consequences to know before you connect a public repository:

- **A commit author is public if the repository is.** Anyone who can read the repository can read the
  address. Users who do not want their address in a public history should hold an account whose email
  is one they are willing to publish.
- **Git history is not editable after the fact.** Removing an address from a merged commit means
  rewriting history, which Charter cannot and will not do.

The commit message is the specification's title and opening paragraph. Nothing about the model, the
adapter, the session, or the tooling appears in it.

## The one outbound call

Charter checks GitHub once a day for a new release, so that your instance does not quietly keep
running a version with a known vulnerability.

What that request is, exactly:

- An unauthenticated `GET` against the public GitHub Releases API for the Charter repository, at
  `https://api.github.com/repos/<owner>/<name>/releases?per_page=30`. The owner and name come from the
  source URL compiled into your build, so a fork checks its own releases.
- Sent once per day, with jitter, and the result cached in your Postgres. The first check waits a
  minute after startup, so a container in a crash loop cannot turn restarts into requests.
- It sends **no data about your instance** — no version, no identifier, no telemetry, and no query
  parameters beyond the page size. It carries no credential and no cookie. The `User-Agent` is the
  constant `Charter`, with no version in it: a version there would tell GitHub which release you run.
  The comparison against your build version happens locally, after the response arrives.
- GitHub sees the source IP address of the request, as it would for any HTTP request from your server.
  That is the entirety of what leaves your network.
- Failures are silent. An air-gapped, offline, or rate-limited instance keeps whatever it last knew
  and does not log an error every day, because logs that cry wolf get ignored. The timestamp shown as
  "last checked" only moves when a check actually reached GitHub — you are never told an offline
  instance was checked an hour ago.

Turn it off with one variable:

```bash
CHARTER_UPDATE_CHECK=false
```

That is not a flag consulted at the moment of the call. With it set, the component that would make the
request is never built, so there is nothing in the running instance capable of contacting GitHub.

`CHARTER_DEMO=true` also disables it, along with every other call Charter would make to a third
party — model providers, GitHub, OAuth, and SMTP included. That is a demonstration mode, not a
hardening setting: it seeds fake data and the instance cannot do real work. If you want a working
instance that does not phone home, `CHARTER_UPDATE_CHECK=false` is the switch to use.

The default is on. An operator unknowingly running a vulnerable version is a worse outcome than one
outbound request a day — but it is your call, and it is a single flag.

Everything else Charter talks to, you configured: your Postgres, your GitHub App, your model provider,
your SMTP server, your log sinks, your runners.

## What goes into an email

Charter sends five kinds of message: an invitation, a password reset, a question about a request,
something ready to try, and the test message you send yourself from settings. Each one carries the
recipient's name, what they asked for in their own words, and a link back to this instance.

None of them carries a repository name, a branch, a commit, a diff, a stack trace, or a cost. The
templates for the two status messages have no field for any of it — a requester never sees those in
the app either, and email is the surface where that is easiest to leak. Free text those templates do
carry is scrubbed of commit hashes, forge URLs, and stack frames before it is rendered.

Mail goes to the server you configured and nowhere else. There is no third-party sending service and
no tracking pixel — the templates load no images at all.

### What Charter keeps about a send

Every attempt is recorded in your own database so that a delivery failure is visible in admin
settings rather than only in a log line somebody has to go looking for. A record holds the recipient
address, the template name, the outcome, and whatever the mail server said if it refused — **no
subject line, no body, and never a credential**.

It is pruned automatically: 30 days, or 2,000 attempts, whichever comes first. Nothing else keeps a
standing list of who this instance has written to.

An invitation is stored the same way you would expect a credential to be. The row holds the invited
address, the roles offered, who sent it, and a SHA-256 digest of the emailed token — **never the
token**. Nobody with a database dump can turn one back into a working invitation link, and nobody
inside Charter can retrieve a link that was lost; it is reissued instead.

## Where observability data goes

Only where you point it. All three log sinks are off unless you enable them, and none of them has a
Charter-operated destination.

| Sink | Enabled by | Destination |
|---|---|---|
| Console | Always on | Your container's stdout, and whatever scrapes it |
| Seq | `CHARTER_SEQ_URL` | The Seq server at that URL |
| OpenTelemetry | `OTEL_EXPORTER_OTLP_ENDPOINT` | The OTLP collector at that endpoint |

Traces, metrics, and logs all travel over OTLP to your own collector — Grafana, Datadog, Honeycomb,
Signoz, or anything else that speaks the protocol.

One thing to know before you wire up a hosted log platform: **transcripts contain your source code and
your business context.** By default Charter logs event metadata only — event type, timing, correlation
id, token counts, cost. Transcript bodies are logged only when you set:

```bash
CHARTER_LOG_INCLUDE_TRANSCRIPTS=true
```

Setting it exports transcript content to every enabled sink, and Charter says so in the startup log
so an instance never does it quietly. If any of those sinks is a third-party SaaS, that is your code
leaving your infrastructure. Enable it for a specific debugging session and turn it off afterwards.

The flag currently governs the model calls Charter's own control plane makes — refinement, teaching,
and the engineer recap. The agent's transcript from a runner is not written to any log sink with the
flag on or off; it lives in your Postgres and in the session's transcript pane. See
[configuration.md](configuration.md) for what that means when you are debugging a runner.

Transcripts, specs, and requests are stored in your Postgres. So is everything around them: the
refinement conversation turn by turn — including what a requester typed in their own words — the
*Works* / *Not quite* verdict and any note that came with it, each person's theme, pane, and teaching
level, and the metadata describing a verification artifact (filename, size, checksum, capture URLs,
test counts, device identifier). None of it leaves your instance. Per-request deletion and
organisation export are first-class features, not support requests. Event retention is configurable
and enforced by a pruning job ([spec §20](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)).

## Object storage, if you configure it

`CHARTER_STORAGE_BACKEND` is `none` by default, and on that setting nothing is written outside
Postgres. Setting it to `filesystem` or `s3` gives Charter somewhere to put the bytes that do not fit
in a row: today, the oversized strings in a transcript event — the file body in an agent's `Write`
tool call, the output of a failing check. The event keeps the tail of that text and a reference to
the rest.

Three things are worth knowing before you turn it on.

**It is your storage, on your terms.** A bucket is reached at the endpoint you configure, with the
credentials you supply. Charter makes no other outbound call on account of it, and the store is never
a third party's by default.

**Reads go through Charter, not through a link.** Stored objects are served from
`GET /api/requests/{id}/blobs/{key}`, behind the same permission as the transcript pane. Charter does
not generate public or presigned URLs and has no setting that would — a link that authorises whoever
holds it would let a pasted URL bypass every permission check. Your bucket should not be public.

**Charter never deletes on a schedule.** Objects are capped at 8 MiB each and keyed under the session
that produced them, but there is no sweeper and no expiry setting: something Charter deleted quietly
would be evidence about a session somebody may still need. Retention is yours to run — a bucket
lifecycle rule, or a cron over `CHARTER_STORAGE_PATH`. If you have a deletion obligation, that is
where it belongs, and the same store is where a per-request deletion has to reach.

---

See [configuration.md](configuration.md) for every variable named here, and
[spec §19 and §28](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) for the underlying design.
