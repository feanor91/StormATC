# Privacy and API keys

> StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.

## The short version

- With no API keys configured, **nothing leaves your computer**.
- Transcripts and session history are stored **on your computer only**, and you can delete them.
- The microphone is open **only while you hold push-to-talk**.
- Raw audio is **never written to disk**.
- StormATC has no accounts, no analytics, and no telemetry.

## What leaves your computer

| Configuration | What is sent, and where |
| --- | --- |
| Nothing configured | **Nothing.** Deterministic phraseology, typed transmissions, entirely offline. |
| Deepgram enabled | Audio captured while push-to-talk is held goes to Deepgram to be transcribed. Controller text goes to Deepgram to be spoken. |
| A language-model provider enabled | The transcript and approved controller facts go only to the selected OpenRouter, OpenAI, Groq, DeepSeek, or Anthropic API. |
| AI traffic wording explicitly enabled | A nearby traffic callsign and coarse observed situation go to the selected language-model API, at most once per generated exchange. |
| Spoken traffic explicitly enabled | The generated controller and pilot lines go to the configured text-to-speech service, as two short requests per exchange. |
| SimBrief configured | Your SimBrief user ID, to fetch your own plan. |
| MSFS connected | Nothing goes to the internet. SimConnect stays local; StormATC reads flight/facility data and sends one local COM1 tuning event when a flight is loaded. |

That table is the whole of it.

Each networked row is independently controlled. Entering a key stores it but does not enable
background traffic requests. Live traffic reading, generated exchanges, AI wording, and spoken
traffic are separate switches under **Settings > Live integrations**; all four start off.
Connecting a simulator sends nothing to the internet.

Generated traffic has a hard hourly exchange ceiling (six by default), waits at least two minutes
between exchanges, yields to user push-to-talk, and pauses by default while a common online ATC
client is running. Local deterministic traffic wording uses no model credits. Spoken exchanges can
make two text-to-speech requests each, so they require a separate opt-in.

### Offline mode

Settings → **Offline mode** stops StormATC making any network request whatsoever. With it on, the
implementations that could reach the network are not constructed at all, and SimBrief is not
contacted even when a user ID is configured.

It is off by default, and that is not a privacy compromise: every row above already requires
something you have to supply, so a default installation sends nothing either way. Offline mode is
for people who want that guaranteed by construction rather than merely true in practice. It is the
one setting that needs a restart, because it decides what gets built.

## What is stored locally

| Path | Contents | How to remove it |
| --- | --- | --- |
| `%LOCALAPPDATA%\StormATC\stormatc.db` | Sessions, transcripts, decisions, clearances, handoffs | Settings → **Clear all local history** |
| `%LOCALAPPDATA%\StormATC\usersettings.json` | Your non-secret settings | Delete the file |
| `%LOCALAPPDATA%\StormATC\secrets.dat` | DPAPI-protected API keys | Settings → **Remove key**, per key |
| `%LOCALAPPDATA%\StormATC\flightplans\` | Raw SimBrief responses, only when caching is explicitly enabled | Delete the directory |
| `%LOCALAPPDATA%\StormATC\logs\` | One log file per day, kept for `Diagnostics:LogRetentionDays` | Delete the directory |

Deleting `%LOCALAPPDATA%\StormATC` entirely removes every trace of StormATC's data. Diagnostics →
**Open data folder** takes you there.

Transcript storage can be turned off completely in Settings.

The log files record what the application did — which provider was chosen, which clearance was
issued, what failed. They contain no API keys, no `Authorization` headers, and no audio.

## Audio

- The microphone opens when you **press** push-to-talk and closes when you **release** it.
  StormATC has no continuously-listening mode and no wake word.
- A transmission is capped at `Deepgram:MaximumTransmissionDuration` (30 s by default), so a stuck
  key cannot stream indefinitely.
- Interim transcripts are shown on screen for feedback and **never reach the ATC engine**. Only
  the final transcript is processed.
- The optional voice trainer opens the selected microphone for one six-second sample and sends
  that audio to Deepgram for transcription. StormATC does not send the sample to the ATC engine
  or save its audio. It stores only the expected phrase and an explicit text correction in
  `usersettings.json`.
- **Raw microphone audio is never written to disk.** The production speech service has no
  recording path. Diagnostics contain timings and provider states, never microphone samples.

## How API keys are held

Keys go into `DpapiSecretStore`, which encrypts them with the Windows Data Protection API scoped
to `CurrentUser`. The ciphertext is bound to your Windows account: another account on the same
machine cannot read it, and neither can a copy of the file taken elsewhere.

StormATC:

| | |
| --- | --- |
| never writes a key to `appsettings.json` or any settings file | `UserSettingsService` refuses credential-shaped keys |
| never stores a key in SQLite | no repository method accepts one |
| never writes a key to a log | `DpapiSecretStore` logs key **names** only |
| never puts a key in an exception message | provider errors report a category, not a response body |
| never includes a key in a diagnostics export | the export records presence, never value |
| never displays a stored key | the Settings page shows "A key is stored (hidden)" |
| stores a key only on an explicit action | saving requires a button click |
| requires consent before using a cloud key | every language-model provider and Deepgram refuses requests until consent is enabled |
| cannot redirect a key to another host | official provider hosts are pinned and HTTP redirects are disabled |

The rolling file logger and diagnostics exporter also apply credential-pattern redaction as a
last line of defence. This supplements the stronger rule that call sites never log a secret in
the first place.

No provider options class has an `ApiKey` property. There is no field for a key to be bound into,
serialised from, or logged out of.

### The password box

The Settings page reads the key from the password box on click, hands it straight to the secret
store, and clears the box. It is never bound to a property, never held in view-model state, and
never enters the visual tree as text — so it cannot be captured by an automation client, a
screenshot of the element, or a serialised binding.

### Removing a key

Settings → **Remove key**. It is deleted from the store immediately. The relevant feature reverts
to its degraded mode: deterministic phraseology without a model provider, typed transmissions and
text responses without Deepgram.

## The diagnostics export

Diagnostics → **Export diagnostics** writes a text report for a bug report. It is assembled line
by line rather than by serialising objects, because a whole-object dump is one careless property
away from leaking a credential.

It contains: version, OS, runtime, provider states, configured model name, simulator state,
database schema version, prompt hashes, startup warnings, and recent latency figures.

It does **not** contain: API keys, transcripts, audio, raw provider responses, or prompt content.

Credentials appear as `Stored` or `Not stored` — presence only.

## Third-party services

OpenRouter, OpenAI, Groq, DeepSeek, Anthropic, and Deepgram are independent companies. When you
enable one:

- You are their customer under their terms, not StormATC's.
- **They bill you according to their own pricing**, which is theirs to set and change. StormATC
  does not resell them and makes no claim about what they cost.
- Their handling, retention, and training use of what you send is governed by **their** privacy
  policy. Read it before enabling them.
- StormATC sends what the table above describes and nothing more, but it cannot control what a
  provider does with it.

The first-run screen states all of this before anything is enabled, and declining is a supported
outcome.

## Model output is untrusted

A privacy point that is also a safety point: everything a model returns is treated as untrusted
input.

- A **parsed intent** is re-derived through the domain value objects. Anything that fails
  validation causes the entire answer to be discarded.
- **Generated phraseology** is checked against the facts the engine approved. Every number in the
  text must be one of them.

A compromised, confused, or prompt-injected model cannot issue a clearance, change your session
state, or make StormATC send data anywhere. The engine decides; the model only speaks.
