# Capture policy — 0.5.0

Toolbar click toggles ON/OFF. No popup, settings page, context menus, intermediary documents, URL display, or custom error page ships in either browser package.

Chrome uses navigation-only DNR block rules. The background matches blocked navigation to its exact suffix/attachment policy, hands off once per request and restores the originating document; fresh download tabs may close or return to blank. Firefox uses a blocking response-header listener returning cancel, and forwards directly to native messaging. Firefox does not attempt to call ungranted Chrome DNR APIs.

Only HTTP(S) navigation to configured non-passive file extensions, an attachment response, or an explicit download click/form is captured. Page resources, manifests, fetch/XHR and ordinary online media are left alone. Other-resource BLOCKED_BY_CLIENT errors are not download evidence. Only navigation request contexts are retained; credentials are scoped to the request URL and tab and discarded on URL changes/OFF. Buffers are bounded; no request context is written to extension storage.

No file-content signature sniffing is used by the desktop filename resolver. It uses response metadata/final URL only.

Native transfer uses browser-owned permission/consent prompts. Firefox requires 140+, declares browsingActivity, websiteContent and authenticationInfo for essential URL/session transfer to the local native app, and optional cookies permission. There is no telemetry or remote analytics backend. An AMO signature is still required for persistent installation in ordinary Firefox; shipped Firefox packages support temporary developer installation.

Limits: arbitrary blob, service-worker and background API downloads cannot all be taken over before browser download creation. Protected media is not bypassed. Cancellation does not replay POST or silently start a browser download. A user can toggle OFF then retry in the browser.

Research references:
- https://developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest
- https://developer.chrome.com/docs/extensions/reference/api/webNavigation
- https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/webRequest/onHeadersReceived
- https://extensionworkshop.com/documentation/develop/firefox-builtin-data-consent/

Validation uses mocked cross-browser APIs plus isolated real Edge and Firefox profiles, production native framing/pipe/engine, local HTTP fixtures and SHA-256 checks. Browser profiles, native registration and temporary listeners are cleaned up. WPF approval is replaced by a deterministic acceptance fixture, so these are not real approval-dialog UI tests.

## 0.5.1 response metadata and availability

An ON transition requires a successful native ping first. An unreachable bridge leaves routing OFF; transport loss also disables routing for subsequent links. No popup, options page, intermediary tab or URL display page is added.

For intercepted navigation responses, only Content-Disposition, Content-Type, unencoded Content-Length and the HTTP status are forwarded, scoped to the exact request ID, tab and URL. These bounded records expire after 30 seconds and are consumed once. Cookies and POST bodies continue to use the existing restricted session channel. The extension performs no extra HEAD, GET or POST request to inspect a link. The desktop may use a cancellable HEAD preview for GET links when no browser response metadata is available; it never probes a POST by replaying it.

Toolbar icons use the established download-arrow design at native toolbar resolutions: blue while ON and gray while OFF, together with the existing ON/OFF badge.

## 0.5.3 browser review visibility

For blocked Chrome navigation, request context is captured before restoring or closing the temporary browser tab. Restoration is awaited before invoking the native handoff, so it does not run after the desktop review opens. The native host grants foreground permission only to the connected named-pipe server process, and only for import/prepare/activate. Ping and transaction completion do not request foreground activation. Desktop reviews use a one-shot initial z-order promotion and restore their original topmost state immediately after the first render or on early close. No focus-stealing interval or global foreground permission is used.
