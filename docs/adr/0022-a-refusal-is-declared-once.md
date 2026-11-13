# A Refusal is declared once

Status: proposed

## Context

A Refusal is the rig declining a command: the camera is busy, a Session is running, the optics are not set. On 2026-11-13 the cause of a Refusal was not a thing in the code. Daemon modules threw `InvalidOperationException` with an English sentence at 108 sites, and the endpoints turned exception types into answers in 37 catch blocks; the plate-solve endpoint reported any such exception that was not a busy camera as `no-camera`. Three wire shapes carried a cause: a token in `title` (six kinds), a `reason` extension (three kinds) or only the `detail` sentence. In the app a widget compared the status code and `reason`, then picked the fix by searching the English message for "focal", "pixel" or "optic". The app showed the daemon's `detail` as written, so a Refusal could not be worded in pt-BR.

## Decision

We decided (2026-11-13, [#1847](https://github.com/open-astro/openastro-ara/issues/1847)) that a Refusal has one stable cause, declared once in the daemon with its HTTP status and the names of its parameters. A daemon module refuses by throwing one exception type that carries the cause, and one handler maps it to the answer. On the wire the cause is the `reason` extension of the problem body, on every Refusal; `title` is a human title and `detail` stays an English sentence for the log. `openapi.yaml` publishes the causes, and a client test fails when the app's list differs. The app decodes a Refusal once in its API layer and words each cause in the arb files. A cause the app does not know, or an answer with no `reason`, shows `detail`, so endpoint families move one per change. A Refusal is a 4xx answer; a device fault and a 5xx keep their own path. The pilot is pointing: solving a frame, Center and Check.

## Rules

1. A daemon module refuses a command by throwing the refusal exception with a declared cause, in every endpoint family that has moved. Scope: `OpenAstroAra.Server/Services/*.cs`. Checked by: review (a `throw new InvalidOperationException` reached from a moved endpoint, or a cause written as a string at the throw site).
2. A cause is declared once with its HTTP status and parameter names, and the refusal handler is the only code that turns a Refusal into an HTTP answer. Scope: `OpenAstroAra.Server/Endpoints/*.cs`, `OpenAstroAra.Server/Program.cs`. Checked by: review (a catch block in a moved endpoint that builds a problem answer, or a status code chosen at a throw site).
3. The cause travels as the `reason` extension, and `title` never holds a token. Scope: `OpenAstroAra.Server/Endpoints/*.cs`, `OpenAstroAra.Server/openapi.yaml`, `design/API_CONTRACT.md`. Checked by: review (a `Results.Problem` in a moved endpoint with a lower-case token in `title`, or a cause absent from `openapi.yaml`).
4. The app's list of causes equals the list in the committed `openapi.yaml`. Scope: `OpenAstroAra.Server/openapi.yaml`, `client/openastroara_client/lib/services/*.dart`. Checked by: review (a cause on one side only, or the pilot's client drift test removed).
5. The app decodes a Refusal in its API layer, and no widget or state file reads a status code, a `reason` string or message text to choose what to do. Scope: `client/openastroara_client/lib/screens/**/*.dart`, `client/openastroara_client/lib/widgets/**/*.dart`, `client/openastroara_client/lib/state/**/*.dart`. Checked by: review (a `statusCode ==`, a `reason ==` against a string or a `contains(` on an error message, on a call to a moved endpoint).
6. Each cause has a sentence in English and in pt-BR, and the daemon's `detail` is shown only for a cause the app does not know or an answer with no `reason`. Scope: `client/openastroara_client/lib/util/friendly_error.dart`, `client/openastroara_client/lib/l10n/app_en.arb`, `client/openastroara_client/lib/l10n/app_pt_BR.arb`. Checked by: review (a declared cause with no sentence in one of the two files, or `detail` shown for a known cause).

## Alternatives rejected

- **A returned result value**: the most explicit, but every method and caller changes, and NINA's engine interfaces signal failure by throwing.
- **An endpoint filter over the existing exception types**: the smallest change, but the cause stays an exception type plus an English sentence.
- **The token in `title`**: one field fewer, but `title` stops being a human title.
- **A new `cause` object**: the cleanest shape, but a third name beside `title` and `reason` while both are retired.
- **A generated client enum**: no hand edit in the app, but a generator step for a short list a drift test already guards.
- **The daemon words the Refusal in the app's language**: one place for text, but translations move into the daemon and away from the arb files.
- **A generic sentence for an unknown cause**: no English on a pt-BR screen, but an endpoint that has not moved loses its specific message.

## Consequences

- A module test asserts the cause and not the sentence, and one test covers the cause-to-wire table.
- The per-endpoint catch blocks go as each family moves, and a status code is chosen in one place.
- A Refusal is worded in the app's language; a new cause needs a sentence in two arb files.
- The wire changes: `reason` is added to more answers and six `title` tokens move to it, so `openapi.yaml` and `design/API_CONTRACT.md` change with each family.
- An app older than the rig shows the English `detail` for a cause it does not know.
- Guider endpoints move after records 0016 and 0020 reshape them, and device commands with record 0011's Device Command.

## How to reverse

Stop throwing the refusal exception in new code and let endpoints catch again. Answers that carry `reason` keep working, since the app falls back to `detail`; what is lost is the single list of causes and the translated sentences.

## Links

- [#1847](https://github.com/open-astro/openastro-ara/issues/1847)
- ADR 0014 (the same choice for WebSocket events)
- ADR 0002 (the REST contract between the daemon and the app)
- [`design/API_CONTRACT.md`](../../design/API_CONTRACT.md)
