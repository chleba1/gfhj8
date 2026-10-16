---
name: spec-ramp
description: Turn a one-line intent into a reviewable spec before any code is written. Use when Samuel describes something he wants built, especially if he is stalled on starting it.
---

# spec-ramp

Research finding #4: a spec is two orders of magnitude cheaper to review than the diff it
produces, and it is where architectural intent actually enters the system. Skipping it
relocates design decisions into generated code, where they are invisible and expensive to
reverse.

**But the standard advice — "the human writes the spec" — is wrong for Samuel.** A blank
page is exactly where ADHD task-initiation stalls. A hard human-authored spec gate would not
add control, it would add a stall point, and the spec would get skipped entirely.

So invert it. Same control point, no blank page:

> **He gives one line of intent. I draft the spec. He approves, edits, or kills it.**

That converts an expensive initiation into a cheap yes/no/tweak decision. He still owns the
architecture; he just never faces an empty file.

## When to run

- He describes something he wants built, in any amount of detail.
- He is circling a task without starting it (see [[adhd-workflow-support]]).
- Before any multi-file change, always.

Do **not** run it for: a one-line fix, a rename, a doc edit, anything already specced.

## Procedure

1. **Take whatever he gave.** One line is enough. Do not interview him first — questions
   before a draft are another blank page.

2. **Draft the spec immediately**, in this shape, and keep it under a page:

   ```markdown
   # <feature> — design spec

   ## Intent
   One paragraph. What changes for the player/user, not how.

   ## Decisions
   - <decision>: <chosen option>, because <reason>
   (This is the part he is actually approving. Make the choices explicit; a spec with no
   decisions in it is a summary, not a spec.)

   ## Out of scope
   Explicit. This is what stops the change widening later.

   ## Done when
   - [ ] <condition a command or test can decide>
   (No "works well" or "feels right" — if only prose can judge it, it will be marked done wrong.)

   ## Band
   A / B / C per the autonomy bands. C means he authors the decision, not me.

   ## Risks
   What breaks silently if this is wrong. Name the failure that CI would not catch.
   ```

3. **Flag what I had to assume.** Every gap I filled gets a line: `ASSUMED: <x>`. He corrects
   the ones that matter instead of answering a questionnaire.

4. **Challenge it** (rule 11). If the ask has a flaw, a cheaper path, or a missed edge case,
   say so in the spec itself, briefly, before building. Agreement is not the default response.

5. **Stop. Wait for approval.** This is the gate. Do not start implementing off an unapproved
   spec, however obvious it looks.

6. **On approval**, write it to the project's docs as `<feature> design spec`, then derive the
   implementation plan as a separate commit — matching the existing repo pattern
   (`docs: ... design spec` then `docs: ... implementation plan`).

## If he stalls at the approval step

Not a spec problem — a task-initiation problem. Switch to [[adhd-workflow-support]]: one
ridiculously small physical action, wait for "next". Do not re-explain the spec or break it
down further unprompted.

## Why this is the gate and not a formality

The failure it prevents is specific: an agent generating a plausible 2,000-line diff that
implements the wrong thing competently. Reviewing that diff costs more than writing it did.
The spec is the cheapest place to catch it, and the only place where "no, not like that"
costs one sentence.
