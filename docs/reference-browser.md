# Reference browser

Harness has a native, Windows-only reference browser backed by Microsoft Edge
WebView2. Open the globe button next to Skills / Activity / Settings. It is an
owned Harness module, not an external Chrome/Edge window or an Electron shell.
It starts only when opened or after approving an agent's browser request.

## Using it

1. Start a new chat, paste your direct reference/video URL and ask the agent to
   examine it. New Codex chats and tool-capable direct API models receive the
   `harness_browser` tool. A model must have verified vision support to request
   screenshots; text inspection remains available without vision.
2. Approve browser access when prompted, or open the browser and enable
   **Agent access**. This permits page text and screenshots to be sent to the
   currently selected provider for this chat.
3. The agent can navigate, inspect visible text/controls, click, type into the
   focused field, scroll, control an accessible HTML video, and capture a frame.
   A screenshot is native image input, not a text path the model cannot see.
4. Watch the browser or follow concise entries in Activity. Turn off Agent
   access, close the browser, or stop the turn to revoke/cancel work. Switching
   chats closes the previous chat's browser. Access is not silently restored
   across app restarts.

The toolbar offers manual back, forward, reload and address entry. Scheme-less
addresses such as `www.example.com` are opened over HTTPS. Browser
credentials/cookies are isolated under `%LOCALAPPDATA%/Harness/browser`; it
does not attach to personal browser profiles or depend on another harness.
WebView2 Runtime is a prerequisite. If it is missing, the browser reports the
failure without stopping other Harness features. Install/repair it using
[Microsoft's WebView2 Runtime distribution](https://developer.microsoft.com/microsoft-edge/webview2/).
Harness does not silently download another browser engine.

## Existing Codex chats

The supported managed runtime's generated schema accepts `dynamicTools` on
`thread/start`, but not `thread/resume`. New tool-enabled native chats retain
their tools when resumed. For an older chat, opening the globe offers to connect
it: visible messages stay intact, the previous provider thread ID is recorded,
and the next send starts a new native session with a bounded continuity brief.
This is an explicit choice, not a lossless provider-history migration. Declining
leaves the existing session unchanged. Starting a new chat is also supported.

## Permissions and safety boundaries

- Only Harness's own visible browser is controllable; no desktop-wide mouse,
  keyboard, shell bridge, arbitrary JavaScript, cookie export or host-object API.
- Agent access is scoped to a chat. Requests from another provider/thread/turn
  are rejected. Tools are serialized, cancellable and time-bounded.
- Ask and Approve-for-me modes ask before clicks, typing and navigation.
  Automatic command risk review does **not** currently review custom browser
  tools. Full access skips those per-action prompts but still requires initial
  browser consent. Read/scroll/video controls follow that browser consent.
- Only HTTP(S) top-level navigation, with no embedded URL credentials. Local
  HTTP dev servers are supported. File/custom-protocol navigation, downloads,
  popups and device/permission requests are blocked. Logins, CAPTCHA, DRM and
  other access restrictions must not be bypassed.
- Page text and screenshots are untrusted task data, not instructions or
  additional user authority. Do not authorize purchases, account changes or
  submissions just because a page asks for them.
- Inspection is bounded and excludes hidden text and form values. Screenshots
  naturally include whatever is visible in this browser; do not enable access
  on a sensitive page unless you intend to share it with your model provider.

## Video truthfulness

Vision is not audio capture or a continuous video feed. The agent can seek,
pause/play and capture individual frames from the first accessible main-page
HTML video; observations report player time, duration, readiness and currently
available caption cues. Visible YouTube transcript text can also be read when
the transcript is opened. Report the timestamps actually examined, not an
unsupported claim to have watched/heard the entire video.

Cross-origin iframe players, unavailable captions, DRM, autoplay restrictions,
live streams without seekable duration and sign-in walls may require manual
interaction or remain inaccessible. No media downloading, audio transcription
or native provider computer-use API is implemented in this slice.

## Performance and verification

Web content runs in WebView2 processes. Its native controller/UI calls stay on
the required STA dispatcher; profile I/O, screenshot base64 encoding, provider
request construction and large native-history serialization run on workers.
Browser screenshot payloads are stripped from Codex completion notifications
before they reach the UI/event log. API image observations remain in native
provider history (and count toward model context); they are not chat bubbles.

Focused offline protocol checks:

```powershell
dotnet run --project tools/Harness.BrowserCheck -c Release
```

Optional native rendering/input check (Windows + WebView2, creates a local
loopback test page and an isolated `.artifacts/browser-check` profile):

```powershell
dotnet run --project tools/Harness.BrowserCheck -c Release -- --native
```

This validates real rendering/input, a synthetic HTML video frame, image
payloads for all four direct API protocols, stale-page checks, revocation and a
dispatcher heartbeat. It makes no model requests. It does not validate a live
YouTube URL or a paid end-to-end model turn.

Implementation references:
[Codex App Server](https://learn.chatgpt.com/docs/app-server),
[WebView2 threading](https://learn.microsoft.com/microsoft-edge/webview2/concepts/threading-model).
