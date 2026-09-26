# Investigation report — structure

The report is what the user acts on; they were probably not watching. Lead with
the verdict, keep each section short, and let the screenshots and the
server-impact section carry the detail. Deliver it in the terminal; publish it
as an Artifact only if the user asks for something shareable.

```
## PR #<n> — <title> (by <author>)

**Verdict:** <Effective — merge-ready / Effective with fixes needed / Partially effective / Not effective / Could not verify>
<one or two sentences: does the change do what the PR claims, and is it safe to land?>

### In plain terms
One paragraph, no file names, no jargon the maintainer would have to look up:
what this PR makes the software do that it did not do before, told as the
sequence a user or the daemon would experience ("run one way it builds the
client and installs it; run the other way it cross-compiles the daemon, copies
it to the box, stops the services, swaps the binaries, restarts and checks
them"). End with one sentence on whose problem it solves and what it sidesteps
or replaces. The maintainer reads this paragraph when they have thirty seconds
and the rest of the report when they have ten minutes.

### What it claims vs what it does
- Claim: … → Verified / Not verified / Contradicted (how you checked)
- …

### In the client  (omit if no client change)
<screenshot path> — caption: what is shown, what changed vs master, what to look at.
<screenshot path> — edge case caption.
Walkthrough: where in the app the change lives (tab → screen → widget), what the
user will notice, anything that looks wrong or unfinished on screen.

### Server impact  (omit if none; be thorough if present)
- Endpoints / contracts touched (design/API_CONTRACT.md if listed there)
- Behaviour change for a running daemon: startup, profile/state files, migrations
- Hardware / Alpaca / guider paths affected, and whether they were exercised
  (local daemon, Pi rig, simulators) or only read
- Deployment / packaging / systemd hardening implications (NoNewPrivileges,
  RestrictAddressFamilies, sudo helpers — the Pi memory lists the traps)
- Risk if wrong, and how you would roll it back

### Findings
Defects (must fix before merge), then Notes (should fix / follow-up). Each with
file:line and a one-line reproduction or reasoning.

### Process state
- CI: <green / red: which jobs>
- Bot review: <verdict on current head / not run — fork PR without safe-to-review label>
- Base drift: <N commits behind master; files overlapping; merge preview clean or conflicting>
- Registry gates: settings (§61.4) / help (§69.4) / docs touched?
- Contributor's own validation claims and what they said they could not test

### Policy fit
Does the PR fit how the project is run, independent of code quality? Known
positions (also in memory): no turnkey install/update/deploy scripts — paying
users get support, everyone else works deployment out with the community;
the APT path in docs/DEPLOY.md is the only documented route. A PR that
collides with a position is "parked on policy" and the report says so first,
before the code findings, so nobody polishes something that will not land.

### Recommended next step
One of: apply `safe-to-review` and let the bot run; request changes (list);
merge under §19.1 (`gh pr merge` without `--delete-branch` for a fork head);
hold for human review — with the reason.
```

Merging, labelling, commenting on the PR, or pushing to the contributor's
branch are all outside this skill. Recommend; do not act.
