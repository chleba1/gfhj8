# AI-Brain CLAUDE.md — rule rewrites

Three edits. Rule 10 is the live bug; 14–17 are context-cost reduction.
Operational content preserved verbatim; incident narrative moved to the notes
that already hold it.

---

## 1. Replace rule 10 entirely

Current rule 10 is stale (prescribes Fable, dead since 2026-08-03), internally
contradicted by its own addendum, and doing four jobs at once. Replacement:

```markdown
10. **Token discipline — route work down, not up (rewritten 2026-10-16,
    supersedes the 2026-07-10 Sonnet/Fable tiering).**
    Main session runs **Opus** (`opus[1m]` in settings.json) — the vault's
    context and MMOCore's architecture work justify it. The old rule had this
    backwards: it assumed a cheap main session escalating upward. The cost
    now lives in *not delegating downward*.

    Before doing a task myself, route it:
    - **Mechanical, clear spec** → Codex (rule 9), else an Agent subagent with
      `model: "haiku"`.
    - **Bounded, needs some judgment** (focused refactor, test writing,
      triage, doc pass) → Agent subagent with `model: "sonnet"`.
    - **Judgment** (architecture, ambiguous requirements, legal, multi-constraint
      design, anything in MMOCore Band C) → keep it in the Opus main thread.

    Never spend the Opus main thread on: docs, commit messages, file plumbing,
    test-watching, cleanup, triage. That list was already in the old rule and
    was being violated continuously — 90/90 tagged MMOCore commits are Opus,
    including `docs:` commits.

    One topic = one session: close with `/brain-end`, continue fresh. The
    session-start ritual rebuilds context for ~2K tokens; dragging a fat
    transcript costs ~100x that per turn. The Stop hook nudges past 5 MB —
    hard signal, not a suggestion. Never run `/brain-away` while a session is
    active (double-burns the same 5h window).

    Model availability is a **standing rule-14 target** — re-verify before
    relying on a tier, don't inherit it from this text. Fable was dead for
    ~10 weeks while this rule still prescribed it.

    Plugin/subagent gating gotchas (CLI escape hatch, `enabledPlugins`
    behaviour): [[firecrawl-cli-discovery-gotcha]],
    `~/.claude/skills/plugin-toggle/SKILL.md`.
```

**Why this shape:** composes with rule 9 instead of duplicating it, names tiers
that actually exist, and makes the staleness that killed the old rule an
explicit rule-14 target rather than a footnote.

---

## 2. Compress rules 14–17

Same imperatives, same operational detail. The `**Why:**` post-mortems move to
the notes already holding them — they load on demand instead of on every
session start.

```markdown
14. **Re-verify stale environment constraints when the world visibly moves
    (2026-07-12).** "X isn't possible (checked <date>)" is a snapshot, not a
    fact. When a later message shows the environment changing — a tool
    installed, a service configured, a dependency added — re-test it myself
    before the next dependent step. Don't wait to be asked; don't route around
    it. Before editing a config to fix a *theorized* cause, rule out the boring
    explanation first ("did I just not wait or retry enough") — a technical
    theory isn't confirmed by being the one I found.
    Standing Kaizen scan target. Cases: [[kaizen-design]] "Samuel-caught gaps",
    [[measure-before-theorizing]].

15. **Execute and assert before calling a machinery change done (2026-08-07).**
    Machinery = `AI-Brain/scripts/`, `~/.claude/{hooks,skills,agents,scripts}/`,
    the settings files, either `CLAUDE.md`. Reading the code is not
    verification. Two hard obligations:
    (a) any new API, `Add-Type` or P/Invoke is executed once in a bare
        `powershell -NoProfile` before the change is called done;
    (b) a probe that can fail must return a value that **cannot be mistaken for
        a measurement** — `$null` or `"unavailable"`, never a plausible number.
    Assert the *mechanism* loaded, not that the output looked plausible. Scripts
    document their own dry-run in the header; run it.
    Scope carve-out: `~/.claude/skills/design-review/SKILL.md`.
    Case: [[verify-the-mechanism-not-the-output]].

16. **Nothing I schedule may show a window (2026-08-16).** Scheduled/background
    work runs invisibly. A console blinking on his screen is my bug.
    Task actions use `wscript.exe "AI-Brain\scripts\run-hidden.vbs" "<script.ps1>" [args]`,
    never `powershell.exe` directly. **`-WindowStyle Hidden` does not work** —
    powershell.exe creates the console then hides it, so it still flashes for a
    frame. `New-ScheduledTaskSettingsSet -Hidden` is unrelated (hides the task in
    the Task Scheduler UI). Keep tasks Interactive — S4U also removes the window
    but breaks toasts, dialogs and the WinRT radio API. Verify by enumerating
    windows by class, not by eye.
    Case: [[powershell-hidden-window-flash-gotcha]].

17. **Never diagnose from a proxy. Read the mechanism (2026-08-16).** File size,
    timestamp, process name, a task's "Ready" state, line count — symptoms, not
    findings. Before asserting the system behaves some way, open the code that
    produces the behaviour or run it and measure. "It looks like X" is a
    hypothesis and stays labelled as one until confirmed. If a claim would change
    what Samuel does, it must trace to something executed or read. State evidence
    inline ("`Write-NetFailLog` caps at 2MB", not "the log is big"). Where only a
    proxy exists, say so rather than rounding it up to a conclusion.
    Case: [[verify-the-mechanism-not-the-output]], [[measure-before-theorizing]].
```

Rough saving: ~2,300 words → ~450 across the four rules, every session.

---

## 3. Wire the Band C gate

`settings.json` already matches `Write|Edit|MultiEdit|NotebookEdit|...` for
`design-review-gate.ps1` (rule 15's note that `Edit` was missing is now stale —
it's present in the live file). Add a **second** `PreToolUse` block scoped to
file edits only:

```json
{
  "matcher": "Write|Edit|MultiEdit|NotebookEdit",
  "hooks": [
    {
      "type": "command",
      "command": "powershell",
      "args": [
        "-NoProfile",
        "-File",
        "C:\\Users\\Easy\\.claude\\hooks\\bandc-gate.ps1"
      ],
      "timeout": 10
    }
  ]
}
```

Install order:

1. `bandc-gate.ps1` → `C:\Users\Easy\.claude\hooks\`
2. `bandc-unlock.ps1` → `C:\Users\Easy\Desktop\MMOCore\scripts\`
3. **Run the dry-run first (rule 15a):**
   `powershell -NoProfile -File C:\Users\Easy\.claude\hooks\bandc-gate.ps1 -SelfTest`
   Expect 7 PASS lines. It asserts the gate logic, including that client UI,
   docs, other repos and `Read` all pass through.
4. Only then add the settings.json block.

Tune the `$BandC` table at the top of the gate once you've seen it fire —
`\\Shared\\.*\.cs$` is the one most likely too broad, since catalogs live there
alongside wire types. Narrow it to the wire-type subfolder if it nags on data edits.
