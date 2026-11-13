# A Prompt is raised in one client state module

Status: proposed

## Context

A Prompt is something the rig's state asks or tells the person: a filter to swap, a device that failed to connect, a Session waiting for the lens cap. On 2026-11-13 the rules for raising one sat in widget lifecycles. The Simple Mode shell registered 8 watchers in `initState` and kept their dedupe in widget fields. Eight listener widgets held the rest, and six of them chose between a dialog and a pill themselves. A pill's action was a closure built in the widget, so Resume called the sequence API from there. One watcher saved the profile: it filled the telescope's aperture and focal length from the mount, only in Simple Mode and only with the app open, while the daemon fills the camera's fields of the same Profile Section. Reaching any of these rules needs a pumped widget tree: `simple_shell_test.dart` is 7,603 lines with 341 widget tests.

## Decision

We decided (2026-11-13, [#1846](https://github.com/open-astro/openastro-ara/issues/1846)) that one client state module holds the open Prompts and their rules. It decides when a Prompt is raised, what makes two the same and when one is withdrawn, and it runs a Prompt's actions. A Prompt carries a kind, its parameters and named actions, and no text: each presentation words the kind through l10n, the Full UI as a dialog and Simple Mode as a pill. Every rig-raised Prompt moves in, a few per change. A Prompt that only Simple Mode shows stays Simple-only. Simple Mode's message card stays as its presentation and still takes one-off feedback for something the person just did. A moved rule's widget tests are rewritten as state tests and the widget versions deleted. The fill of aperture and focal length from the mount moves to the daemon, beside its camera fill.

## Rules

1. A rig-raised Prompt is raised, deduplicated and withdrawn by the Prompt module, and no widget watches rig state or a WebSocket event in order to show one. Scope: `client/openastroara_client/lib/screens/simple/simple_shell.dart`, `client/openastroara_client/lib/widgets/*_listener.dart`. Checked by: review (a `listenManual` or an event match in a widget that ends in `showDialog` or a post to the message card, for a Prompt kind the module already holds).
2. A Prompt carries a kind, its parameters and named actions, and holds no display text, no `BuildContext` and no closure. Scope: `client/openastroara_client/lib/state/*.dart`. Checked by: review (a Prompt field of type `String` meant for display, or of a function type).
3. A presentation passes the name of an action to the Prompt module, which runs it; no presentation calls the rig for a Prompt's action. Scope: `client/openastroara_client/lib/screens/simple/simple_message_pills.dart`, `client/openastroara_client/lib/screens/simple/simple_shell.dart`, `client/openastroara_client/lib/widgets/*_listener.dart`. Checked by: review (an API call inside a Prompt's button handler).
4. Each Prompt kind is worded in the arb files, in English and in pt-BR, by the presentation that shows it. Scope: `client/openastroara_client/lib/l10n/app_en.arb`, `client/openastroara_client/lib/l10n/app_pt_BR.arb`. Checked by: review (a Prompt kind with no sentence in one of the two files).
5. The message card takes a direct post only as feedback for the person's own action. Scope: `client/openastroara_client/lib/state/simple_messages_state.dart`, `client/openastroara_client/lib/screens/simple/*.dart`. Checked by: review (a direct post whose trigger is rig state or a WebSocket event).
6. A rule that has moved into the Prompt module is tested there with fake rig state, and each presentation keeps one widget test per Prompt kind. Scope: `client/openastroara_client/test/screens/simple/simple_shell_test.dart`, `client/openastroara_client/test/widgets/*_listener_test.dart`. Checked by: review (a widget test that asserts when a moved Prompt is raised or withdrawn).
7. No widget saves a Profile Section in reaction to rig state. Scope: `client/openastroara_client/lib/screens/**/*.dart`, `client/openastroara_client/lib/widgets/**/*.dart`. Checked by: review (a save to the rig's profile inside a provider listener).

## Alternatives rejected

- **Only the Prompts both presentations show**: a smaller module, but the shell keeps its 8 watchers and its test does not shrink.
- **A Prompt holds finished text and closures**: less to change, but the module needs the locale and its tests assert on English sentences.
- **Every message goes through the module**: one path, but the module grows kinds that are not rig state.
- **Keep the widget tests and add state tests**: nothing deleted, but each rule is tested twice and the widget test stays the slow path.
- **The client fills the optics, from shared state**: no daemon change, but the Profile Section keeps two owners and the fill needs an app open.

## Consequences

- A Prompt's rule is tested with a provider container and no widget.
- The Full UI gains one dialog host, and the listener widgets go as their Prompts move.
- The shell and its test shrink with every slice; a moved rule's widget tests are deleted in the same change.
- A new Prompt kind needs a sentence in two arb files for each presentation that shows it.
- The daemon gains the fill of aperture and focal length from the mount, so it also runs for the Full UI and with no app open.

## How to reverse

Move each rule back into the widget that shows it and give the message card its closures back. The state tests have to be rewritten as widget tests. The daemon's optics fill can stay.

## Links

- [#1846](https://github.com/open-astro/openastro-ara/issues/1846)
- ADR 0009 (behaviour both shells need lands in the daemon or in shared client state)
- ADR 0013 (presentations render shared client modules)
- ADR 0014 (the events some Prompts are raised from)
