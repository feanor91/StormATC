# Project governance

StormATC is an open-source community project. The goal is to let contributors meaningfully shape
the app while keeping releases reliable, secure, original, and simulation-only.

## Roles

### Contributors

Anyone who reports a reproducible bug, improves documentation or design, tests a build, answers a
question, reviews a change, or submits code is a contributor. No invitation is required.

### Reviewers

Reviewers are trusted contributors who regularly help triage issues and review pull requests.
They may be granted GitHub's **Triage** permission without receiving permission to publish code.

### Maintainers

Maintainers can merge changes and manage releases. A contributor may be invited to become a
maintainer after a sustained pattern of:

- technically sound, well-tested contributions;
- constructive reviews and respectful communication;
- careful handling of security, privacy, licensing, and the deterministic ATC boundary; and
- following through on maintenance work after a feature is merged.

Maintainer access is based on trust and availability, not contribution count alone. Access may be
reduced when someone is inactive for a long period or no longer wants the responsibility.

## How decisions are made

Small, reversible implementation decisions are settled in pull-request review. Larger decisions
should start as a GitHub Discussion or design issue before substantial code is written. Examples
include:

- a new simulator or cloud provider;
- changes to persisted data;
- new network destinations;
- moving authority from the deterministic engine to a model;
- a large UI navigation change; or
- a change that affects licensing or redistribution.

The preferred outcome is rough consensus supported by tests, user impact, and the architecture.
When consensus is not possible, maintainers make the smallest reversible decision that preserves
security and project direction. The repository owner has the final decision for release,
credential, legal, and community-safety matters.

## Pull requests and releases

- At least one approving maintainer review is required before merge.
- The author cannot approve their own change.
- Required CI, tests, and the repository safety scan must pass.
- Security fixes may use a private fork or security advisory until disclosure is safe.
- Releases are created from immutable semantic-version tags and must correspond to reviewed
  source.

Maintainers may merge an urgent security or build repair before ordinary review, but must explain
the exception afterward.

## Project direction

[ROADMAP.md](ROADMAP.md) describes priorities, not exclusive ownership. Contributors may propose
work outside the roadmap. Maintainers will explain when an idea does not fit rather than leaving
it unanswered indefinitely.

Governance changes use the same pull-request process as code changes.
