# Maintainer guide

This checklist turns the repository files into an active, welcoming GitHub project.

## After creating the public repository

1. Enable **Discussions** under **Settings → General → Features**.
2. Open **Settings → Pages** and set **Source** to **GitHub Actions**. The included Pages workflow
   publishes the landing site after each change under `website/`.
3. Add the deployed Pages URL to the repository's **Website** field.
4. Create and pin a welcome discussion explaining the project direction and linking to
   `ROADMAP.md`.
5. Enable **Private vulnerability reporting** under **Settings → Security → Code security**.
6. Create these labels:
   - `good first issue`
   - `help wanted`
   - `needs design`
   - `needs reproduction`
   - `security`
   - `area: speech`
   - `area: atc-engine`
   - `area: simulator`
   - `area: ui`
   - `area: documentation`
7. Convert several roadmap items into small issues with an expected result and test guidance.
8. Add repository topics such as `msfs`, `flight-simulator`, `atc`, `wpf`, `dotnet`, and
   `simconnect`.

## Protect the default branch

Create a branch ruleset for `main` that:

- requires pull requests;
- requires the `build-and-test` CI check;
- requires conversations to be resolved;
- blocks force pushes and branch deletion; and
- lets the repository owner bypass the rule only for genuine recovery or security work.

Start with one required approving review. Increase the requirement after the reviewer group grows.

## Triage rhythm

At least weekly when the community is active:

- acknowledge new issues and ask for missing reproduction details;
- close duplicates with a link to the existing discussion;
- move early ideas to Discussions;
- label small, unclaimed tasks;
- review stale pull requests before starting new maintainer work; and
- thank people for useful testing and reviews, not only merged code.

Do not assign an issue to a contributor without asking. If someone claims work and then goes quiet,
politely make it available again after a reasonable period.

## Growing maintainers

Use **Triage** permission for trusted issue and discussion helpers before granting write access.
Invite recurring contributors to review changes in their areas. Maintainer invitations should be
discussed privately by current maintainers and follow [GOVERNANCE.md](../GOVERNANCE.md).

Never share release credentials, API keys, certificate files, or certificate passwords through an
issue, pull request, chat, or repository secret visible to untrusted workflows.
