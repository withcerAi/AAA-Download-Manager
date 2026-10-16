# Code signing policy

## Current status

The project has no publicly trusted code-signing certificate or signing service
approval. Preview packages may be unsigned or explicitly marked self-signed in
their release notes. A self-signed signature does not establish a trusted publisher
on other computers and may still produce Windows security warnings. It must never
be described as CA-trusted signing. Private signing keys are not published.

## Responsibilities

- Repository owner and initial maintainer: [withcerAi](https://github.com/withcerAi).
- Changes from outside contributors must be reviewed before release.
- Any future signing request must be approved by an authorized maintainer.
- Maintainers must enable multi-factor authentication before requesting access to
  a signing service. This document does not certify their current account settings.

## Release requirements

Build release binaries from an identified source commit on a trusted CI runner.
Keep signing credentials in the signing service or protected CI secrets, never in
source control. Use explicit manual approval for signing. Only first-party output
may be signed under the project's signing identity; upstream components keep their
own identities and licenses. Record the source commit, build run, hashes and
signature verification result with each signed release.

SignPath Foundation eligibility and acceptance are separate from publishing this
repository: <https://signpath.org/terms.html>. Add provider attribution only after
the project has actually been accepted and the service is in use.

## Privacy

The application sends download requests to user-selected sources and redirects,
and uses the operating system's normal networking services. Browser handoff is
local through native messaging. No project telemetry or analytics service is
configured. Updates to networking or data collection behavior must update this
policy. See [SECURITY.md](SECURITY.md).
