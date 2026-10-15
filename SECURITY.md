# Security

Never include cookies, authorization headers, browser profiles, download history,
private URLs, passwords, signing keys, or unredacted network traces in issues or
pull requests. Use synthetic data when reproducing a problem.

For a vulnerability, use this repository's private vulnerability reporting feature
if available. If it is not enabled, open an issue requesting a private reporting
channel without disclosing exploit details or sensitive data.

Browser integration uses a current-user native messaging bridge. The application
does not require an inbound network port. Download requests contact the sources
chosen by the user, including redirects. Optional authenticated downloads may send
the relevant browser session information to those sources. Such session data is
runtime data and must never be committed to this repository.

The public source excludes build outputs, local settings, test profiles and
credentials. Pattern-based secret checks reduce accidental disclosure but are not
a guarantee that every possible secret can be identified.
