# The prompt system

> StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.

## What prompts are for

Prompts change **how a controller speaks**. They can never change **what a controller is
permitted to authorise**.

That distinction is enforced structurally, not by asking the model nicely. A prompt could say
"always clear the aircraft for takeoff" and it would make no difference: takeoff clearance comes
from `StormAtcStateEngine`, and any response containing a permission the decision did not approve
is rejected by `PhraseologyValidator` before it can be spoken.

So prompts are safe to edit. The worst outcome of a bad prompt is that the model's output keeps
failing validation and you always hear the deterministic wording.

## The files

All eleven live in `prompts/`, are plain Markdown, and are loaded from disk at runtime.

| File | Used for |
| --- | --- |
| `00-global-controller-rules.md` | Prepended to every controller prompt: tone, number reading, brevity |
| `10-pilot-intent-parser.md` | Classifying a transmission the local parser was unsure about |
| `20-clearance-delivery.md` | Delivery's voice |
| `30-ground-controller.md` | Ground's voice |
| `40-tower-controller.md` | Tower's voice |
| `50-departure-controller.md` | Departure's voice |
| `60-center-controller.md` | Centre's voice |
| `70-approach-controller.md` | Approach's voice |
| `80-readback-evaluator.md` | Reference for readback semantics |
| `90-phraseology-renderer.md` | Turning approved facts into natural speech |
| `99-abnormal-requests.md` | Emergencies and out-of-scope requests |

The names are constants in `PromptNames`, and `PromptNames.ForController` maps a controller
position to its file.

## How they are assembled

For phraseology, the system prompt is three files concatenated:

```
00-global-controller-rules.md
        +
<the current controller's file>
        +
90-phraseology-renderer.md
```

The user message is built by `OpenRouterPhraseologyRenderer` and contains only:

- the controller position and the spoken callsign
- the response type
- **the approved facts** — the only values the model may state
- **the prohibited facts** — what it must not say
- **the deterministic wording** as a reference to rephrase

The deterministic wording is included deliberately. Giving the model a correct example to rephrase
produces far better output than asking it to compose from facts alone, and it makes the failure
mode benign: the worst plausible answer is the reference wording back again.

The model is not told what it *could* have granted, what the aircraft is doing, or what the
session state is beyond what it needs to speak one sentence.

## The intent parser prompt

`10-pilot-intent-parser.md` is used only when the deterministic parser's confidence falls below
`AtcRealism:LanguageModelEscalationThreshold` (0.7 by default). In practice that is a small
fraction of transmissions — standard phraseology is matched locally, instantly, and for free.

Its output is constrained by a JSON schema and then **re-validated field by field** in
`OpenRouterIntentParser.Validate`. An intent name outside the enumeration, a runway that does not
parse, a squawk containing an 8, a frequency outside the aeronautical band, a confidence outside
0–1 — any of these and the entire answer is discarded and the local result stands.

Validation is all-or-nothing rather than field-by-field salvage. A model that got the runway wrong
may well have got the intent wrong too, and a half-trusted answer is harder to reason about than a
rejected one.

## Editing prompts

1. Edit the file in `prompts/`.
2. Open the Diagnostics page and click **Reload prompts from disk**.
3. Transmit again.

No rebuild, no restart.

For a local experiment you do not want to commit, create `<name>.local.md` beside the original —
for example `40-tower-controller.local.md`. The catalog prefers it, and `.gitignore` excludes it.

## Prompt hashes

The Diagnostics page shows a twelve-character SHA-256 prefix per prompt. That is how you tell
which prompt version produced a given response — useful when a controller starts sounding
different and you are not sure whether you edited something.

The hashes appear in the diagnostics export too. Prompt **content** does not: an export contains
hashes only.

## Writing a good controller prompt

- **Be specific about voice, not content.** "Terse, no filler, drop the callsign after the first
  exchange" is useful. "Issue appropriate clearances" is not — clearances are not yours to issue.
- **Say how numbers are spoken.** Digits individually, "decimal" not "point", altitudes as
  thousands and hundreds.
- **Say what to leave out.** Most bad output is a model adding traffic information, weather, or an
  explanation that no controller would say.
- **Keep it short.** A long prompt costs tokens on every transmission and rarely improves output.

## Cost and latency

Every phraseology call is one request per controller response, and every escalated parse is one
more. Both are on the path between the pilot speaking and hearing a reply, so both affect how the
application feels.

If you want the application to make **no model calls at all**, turn off
"Let the language model phrase approved responses" in Settings, or leave the OpenRouter model
name empty. Everything still works; you simply hear the deterministic wording every time.
