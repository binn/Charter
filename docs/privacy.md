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

## The one outbound call

Charter checks GitHub once a day for a new release, so that your instance does not quietly keep
running a version with a known vulnerability.

What that request is:

- An unauthenticated `GET` against the public GitHub Releases API for the Charter repository.
- Sent once per day, with jitter, and the result cached in your Postgres.
- It sends **no data about your instance** — no version, no identifier, no query parameters. The
  compiled-in build version is compared against the response locally.
- GitHub sees the source IP address of the request, as it would for any HTTP request from your server.
  That is the entirety of what leaves your network.
- Failures are silent. An air-gapped, offline, or rate-limited instance does not log an error every
  day, because logs that cry wolf get ignored.

Turn it off with one variable:

```bash
CHARTER_UPDATE_CHECK=false
```

`CHARTER_DEMO=true` also disables it, along with every other outbound call.

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

One thing to know before you wire up a hosted log platform: **agent transcripts contain your source
code and your business context.** By default Charter logs event metadata only — event type, timing,
file paths, cost. Transcript bodies are logged only when you set:

```bash
CHARTER_LOG_INCLUDE_TRANSCRIPTS=true
```

Setting it exports transcript content to every enabled sink. If any of those sinks is a third-party
SaaS, that is your code leaving your infrastructure. Enable it for a specific debugging session and
turn it off afterwards.

Transcripts, specs, and requests are stored in your Postgres. So is everything around them: the
refinement conversation turn by turn — including what a requester typed in their own words — the
*Works* / *Not quite* verdict and any note that came with it, each person's theme, pane, and teaching
level, and the metadata describing a verification artifact (filename, size, checksum, capture URLs,
test counts, device identifier). None of it leaves your instance. Per-request deletion and
organisation export are first-class features, not support requests. Event retention is configurable
and enforced by a pruning job ([spec §20](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)).

---

See [configuration.md](configuration.md) for every variable named here, and
[spec §19 and §28](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) for the underlying design.
