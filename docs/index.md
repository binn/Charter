---
layout: home
title: Charter
titleTemplate: Charter your projects.
description: Charter turns a plain-English request into a pull request, a preview link, and an explanation of what changed — without the person who asked ever opening GitHub. Self-hosted, AGPL-3.0, pre-1.0.

hero:
  name: Charter
  text: Charter your projects.
  tagline: Turn "can the quote tool remember the last thing I picked?" into a pull request, a live preview link, and a plain-English explanation of what changed — without the person who asked ever opening GitHub.
  image:
    light: /charter-mark.svg
    dark: /charter-mark-dark.svg
    alt: ''
  actions:
    - theme: brand
      text: Get started
      link: /getting-started
    - theme: alt
      text: How the loop works
      link: /the-loop
    - theme: alt
      text: View on GitHub
      link: https://github.com/binn/Charter

features:
  - title: It refuses to build vague things
    details: Refinement is a conversation, not a form. If the request is still ambiguous, no agent runs and no tokens burn. The requester approves a written spec with acceptance criteria before anything starts.
  - title: There is no merge button
    details: Charter opens pull requests. Your branch protection and CODEOWNERS decide what ships. Merge authority lives entirely outside Charter's trust boundary.
    link: /security
    linkText: Read the threat model
  - title: Guardrails live in your repo
    details: Path scopes, denied directories, and validation commands live in a committed .charter/config.yml. Widening what the agent may touch takes a pull request and a review.
    link: /charter-folder
    linkText: The .charter/ folder
  - title: Ask, plan, then build
    details: Chat mode answers questions about a project without touching anything, and a surprising number of requests die there. Plan mode explores tradeoffs before a token is spent building.
  - title: Bring your own everything
    details: Anthropic, OpenAI, Gemini, Grok, OpenRouter, or any OpenAI-compatible endpoint. Agent support is declarative YAML, so a new CLI is a config file rather than a release.
    link: /adapters
    linkText: Agent adapters
  - title: It collects nothing
    details: No usage analytics, no phone-home, no opt-out required. Observability data goes only where you point it. The single outbound call is a daily release check, and it can be turned off.
    link: /privacy
    linkText: What Charter never collects
  - title: Two external dependencies, one container
    details: An HTTP port and a Postgres URL. No Redis, no second service to babysit. Every session is fully resumable from the database, because PaaS containers restart whenever they feel like it.
    link: /self-hosting
    linkText: Deployment guide
  - title: Runs where your platform lets it
    details: GitHub Actions by default, so Charter works on platforms that prohibit privileged containers. Docker on a VPS, or a Charter Agent on your own hardware, when you want speed and caching.
    link: /runners
    linkText: Choosing a backend
---

## Status

Charter is **pre-1.0 and under active development**. Expect breaking changes between releases, read
the release notes before upgrading, and take a backup before any upgrade that touches the database.

Phase 1 — the whole loop from a typed request to a preview link — is built and is drivable from a
browser: you can claim an instance with the setup token, sign in, connect a repository, file a
request, and watch the session push a branch and open a change request.

The caveat that matters most: **the loop has never been run against a real repository with a real
model.** Everything is verified against a local database, stubbed providers, and a real git binary
pushing to a local remote. [Getting started](/getting-started) lists exactly what works today, and
[the loop](/the-loop) explains where it stops and why.

Documentation describes the design as specified, and some of it runs ahead of what is implemented
today. Where a capability degrades or does not work at all, the relevant page says so rather than
glossing over it.

## Where to start

If you are standing an instance up, read [Getting started](/getting-started), then
[Self-hosting](/self-hosting) and [Configuration](/configuration) for platform detail. If you are
deciding whether Charter is safe to point at your code, start with [the loop](/the-loop), the
[threat model](/security) and [Privacy](/privacy). If you are integrating with it, the
[HTTP API](/api) is the surface.

Charter is licensed [AGPL-3.0-only](https://github.com/binn/Charter/blob/master/LICENSE). The name
and the mark are not covered by that licence — see
[TRADEMARK.md](https://github.com/binn/Charter/blob/master/TRADEMARK.md).
