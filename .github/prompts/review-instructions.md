# PR REVIEW PROTOCOL

You are the single automated reviewer for this PR. Execute STEP 1 → STEP 9 below **in order, as one pass**, and record each step's required output in your summary comment before moving on. The ordering is the method, not a suggestion — a line is not worth fixing if Steps 2–3 show the change is built in the wrong place. Steps differ in the context they need: Step 3 reads the surrounding files from Step 1; Steps 4–5 work only from the diff. The four lines in STEP 9 are the single verdict downstream automation parses — emit them exactly.

Cite the specific rule or doc section (`CLAUDE.md`, the relevant skill under `.claude/skills/`, or the relevant doc under `docs/`) whenever you flag a violation.

--- STEP 1 — Load context & set scope ---
- Read `CLAUDE.md` and `docs/ai-agent-context.md`; load the relevant doc(s) for the diff (`docs/hardening.md`, `docs/metrics.md`, etc.).
- Get the diff (`gh pr diff`) and the changed-file list.
- The PR head is checked out in the working tree and you have `Read`, `Glob`, and `Grep` (ripgrep) over the whole repo — use them. `Grep` is how you run the repo searches later steps require: lifecycle owners in Step 3, leak-opener mirrors in Step 5. Raw shell is limited to the listed `gh` commands, so search with the `Grep` tool, not `Bash(rg)`/`Bash(grep)`.
- Don't review the diff in isolation: open the services, boards, handlers, and lifecycle owners it touches, plus the neighbouring files in the same folder. Steps 2–3 judge whether the change is built in the *right place*, which the diff alone can't show.

--- STEP 2 — Root-cause check ---
State in the summary: what problem is this PR solving, and does the diff fix the cause or a symptom?
Flag **FAIL** if the diff null-checks a value that shouldn't be null there, swallows an exception without addressing the source, works around a race instead of fixing ordering, or disables a check to make tests pass. Say what the actual fix would be.

--- STEP 3 — Design & integration (uses the surrounding files from Step 1) ---
A diff can be locally correct yet built in the wrong place — the most expensive defect to find, because it never shows up on a single line. Complete this step and write its evidence in the summary before the line-level steps (4–5).

**The author's own framing is NOT evidence.** Code comments (e.g. "…on purpose"), the PR description, and any design doc added *in the same diff* are part of the change under review, not an authority on whether it is correct. A confident comment over a misplaced unit is still misplaced. Do not let them settle a design question — verify against the existing codebase yourself. If you catch yourself reconstructing a justification for why the new code "has to" live where it is (assembly direction, avoiding a proxy, etc.), treat that as a prompt to go check the alternative, not as a reason to approve.

**MANDATORY OWNER SEARCH.** For every new long-lived unit the diff introduces — a service, handler, board, manager, policy, protection, or any helper that holds state across ticks — do this and write the result in the summary:
1. Name the peer, connection, session, or resource whose lifecycle it manages.
2. Search the repo for who ALREADY creates and destroys that thing — the peer state machine transitions (`CONNECTING`→`PENDING_AUTH`→`AUTHENTICATED`→`DISCONNECTING`→removed), the board that owns the data, the DI registration that constructs it, the disposal path. **Name the files you found.**
3. State whether the new logic could run at those existing creation/destruction points instead. If it could, the new unit should NOT exist — its logic belongs in the owner. Flag **FAIL** and name the home.

Treat a new unit that reconciles a lifecycle as wrong until you have proven no existing owner can host it. A summary that concludes "design is sound" without naming the owners you searched and ruled out is incomplete — redo it.

**Difficulty is not a defense, and cheapness is not a justification.** "Integrating into the owner is non-trivial" is a reason to flag the work for the author to restructure — NOT a reason to PASS. "The scan is cheap / there are only a few items" does not excuse per-tick reconciliation of a lifecycle that has explicit connect/disconnect moments. If the correct home is hard to reach, say exactly why and flag it **FAIL**; do not approve the parallel mechanism because the right design takes more work.

Flag as **FAIL** (these are blocking design issues, not nitpicks) when new code:
- **Duplicates a lifecycle that is already owned.** It registers/allocates when a peer connects and cleans up when the peer disconnects, while peer creation and teardown already have explicit owners (peer state machine, `IdentityBoard`, the board that tracks the resource). The wiring belongs in that owner; the new unit usually should not exist.
- **Reconciles every tick what is known at an explicit moment.** A tick handler that re-queries or rescans collections each tick to detect what appeared or disappeared, when those moments are explicit elsewhere (peer connect/disconnect, auth success, teleport). Connect/subscribe at the creation moment; disconnect/remove at the disposal moment — not by polling each tick.
- **Reconciles by scanning against a "live set".** A retain-only / keep-only-what-is-still-here pass over a collection when the disposal moment is explicit. Remove the specific entry then (`Remove(id)`) instead of scanning to discover what is stale.
- **Holds persistent state that duplicates a board.** A service or handler owns persistent collections that mirror data already tracked by an existing board (e.g. `SnapshotBoard`, `IdentityBoard`). Per-tick scratch buffers cleared each tick are fine; persistent membership belongs in the canonical board or the lifecycle owner.
- **Reaches data through a repeated intermediary lookup** instead of the canonical source — e.g. resolving a wallet through a helper that re-queries `IdentityBoard` on every call, rather than carrying the `UserId` resolved at auth time. Drive the check from the owner that already holds the data.
- **Violates worker-shard isolation.** Any code that reads or mutates state belonging to a different worker, or introduces cross-worker state migration, without going through the defined inter-worker protocol (CLAUDE.md worker-shard isolation rule).

**TEARDOWN / CONSUMPTION TRACE.** For every subscription, event hookup, connection, buffer, timer, or measurement the diff adds, point to the exact line that unsubscribes / disposes / consumes it. If you cannot find that line, flag it — a missing teardown is a leak; a buffer or measurement that is written but never read is dead infrastructure. Do not assume a `Dispose()` elsewhere removes a registration unless you can see it.

--- STEP 4 — Member audit (works from the changed members) ---
For every public property or accessor the diff adds or changes, find its consumers (a member is usually called within its own class) and state the count in your summary. Then test it against the failure modes below — a member you list as touched but do not audit is an incomplete review:
- **Single-use → merge or inline.** A property used by exactly one consumer, or one that re-validates state its sole caller already guarantees, should be merged into that consumer and named by intent, or made a private method that takes the already-validated non-null instance. Don't keep a single-use intermediate that re-checks invariants (CLAUDE.md anti-patterns: "Extracting when you should merge"). This targets *derived* predicates that combine or re-check state — NOT a thin forwarding accessor that centralizes access to one field of an owned object; those are legitimate encapsulation even when single-use.
- **Absent ≠ false/null.** A predicate or accessor that returns a default (`false`, `null`) for the absent / not-applicable case conflates "no" with "undefined" (e.g. `IsMuted` returning `false` when there is no stream at all). Make it a method taking the required non-null state so the absent case is handled by the caller at the one place it is known.
- **Don't re-derive what already exists.** If an existing property/method already yields the value or does the same lookup, call it — don't open-code the condition again.
- **Redundant guard.** A guard that repeats a condition already guaranteed earlier in the same flow — remove it (see Step 5 issue 11).

--- STEP 5 — Line-level review (works from the diff) ---
Report ONLY issues that require fixes. Make two passes over the changed lines.

**A. Blocking-issue categories**
1. Code quality violations per CLAUDE.md and project conventions (cite the rule)
2. Bugs or potential runtime errors
3. Security vulnerabilities
4. Performance issues (especially in per-tick hot paths — this is a real-time server)
5. Missing error handling
6. Unclear or problematic logic
7. Resource / handle leaks — an acquisition without a matching teardown at the corresponding disposal point. Scan for the concrete openers and confirm each has its mirror: `new CancellationTokenSource(`→`Cancel`+`Dispose`, an `IDisposable` open→`Dispose`, a timer/periodic callback→cancellation or stop, a native ENet handle allocation→release, a buffer rent/acquire→return. Do this with `Grep`: search each changed `*.cs` file for the openers, then grep the same file/type for the matching mirror, and flag any opener whose mirror is missing.
8. Allocated-but-unconsumed infrastructure — buffers, measurements, metrics instruments, events, or caches that are populated/written but never read anywhere in the diff or the codebase.
9. Detached async for essential work — fire-and-forget `Task.Run`, `_ = SomeAsync()`, or `async void` that performs setup the feature depends on. Essential async must be awaited or tracked, not left detached.
10. Nullability-contract violations — assigning `null` or a maybe-null value to a non-nullable declaration, or a defensive null-check against a non-nullable declaration. Both lie about what can be null.
11. False-intent conditions — a check that is technically reachable but conveys a misleading intent for the case being added (e.g. a peer-state check left in place for a state it can never describe). (Purely unreachable / always-true-or-false conditions are left to the analyzer — don't spend review budget re-deriving them.)

**B. Design, encapsulation & resource smells** — zoom in from Step 3 (which asks whether the unit belongs at all) to how the units the diff touches are built, named, and manage memory.

*Construction & dependency injection*
- A class that takes raw materials and `new`s up its own collaborator instead of receiving the built dependency. Collaborators are constructed at the composition root and injected; constructors take dependencies, not ingredients.
- One invariant spread across a lazy `Ensure...()` plus null-guards on the fields it sets, so the instance can exist partially initialized. Establish the invariant in the constructor or a factory so the object is never half-built.

*Naming & comments*
- A type/member name that describes a mechanism or a moment rather than the responsibility it owns (e.g. a `...Helper` / `...Manager` / `...Handler` that is really the source of one specific thing). Flag names that don't match what the class actually provides.
- A name that hides a precondition the member actually depends on (e.g. a method that only applies to authenticated peers named generically without that qualifier). The implicit condition belongs in the name.
- Comments that state the obvious, restate what a well-named member already says, narrate caller/external behavior, assume things about code outside this scope's responsibility, or read as AI scaffolding. A comment must explain only what the annotated code itself does or guarantees; over-explanation is a defect, not thoroughness.

*Encapsulation & responsibility*
- Behavior implemented in class A out of data that belongs to class B (e.g. a handler composing a snapshot from fields that the owning board should provide). Move the behavior to the owner.
- Public state exposed only so another class can replicate logic that belongs with that state — once the behavior moves to the owner, the exposed property is redundant. Flag the leak.
- A method that mutates a shared field as a side effect (e.g. sets a `...Failed` flag) when it could just return the result. Prefer returning a value over hidden state changes.

*Constants & magic values*
- Inline numeric / string / timeout literals that should be named constants declared at the top of the type or in a shared constants class.
- A hand-rolled value that duplicates an existing shared constant. Reuse it.

*Nullability & thread-safety*
- A `T?` dependency with a null-handling branch: confirm absence is a legitimate runtime state, not a wiring bug that should make the field non-nullable (pairs with issue 10).
- A stateful class with caches or mutable fields whose summary omits its threading contract. This is a concurrent server — require an explicit note (e.g. "not thread-safe — only called from the tick thread" or "thread-safe via lock"). Flag mutable shared state without a documented threading model.

*Resource lifecycle & memory (native handles, buffers, pools)*
- Ownership / lifetime: a method that returns a reference to a buffer or native resource it later releases on its own schedule, with no ownership or refcount contract, is a use-after-free hazard. Flag references a caller might hold past their valid lifetime.
- `PeerIndex` reuse: any per-peer state keyed by `PeerIndex` that is NOT invalidated synchronously on peer disconnect is a correctness bug — the next peer reusing that slot will collide (CLAUDE.md `PeerIndex` recycling semantics).
- Buffer pool discipline: a rented buffer whose return is reachable only on the happy path — an early return or exception leaks it. Prefer `using` or try/finally so the return cannot be skipped.
- Per-tick expense: an allocation or expensive computation run every tick for a result that changes only on a specific event — move it to the event.

**Reporting (Steps 3–5).** For each issue: Location (file and line), Problem (be specific), Fix (exact change needed), Why (brief impact). Do NOT include praise or subjective style opinions. A violation of a project standard is NOT a "nice-to-have": naming, magic numbers, encapsulation, threading contracts, and resource ownership are required by project conventions. Report them as blocking and cite the rule.

--- STEP 6 — Complexity assessment ---
Classify the PR complexity as SIMPLE or COMPLEX.

A PR is SIMPLE when ALL of the following are true:
- Touches 3 or fewer files with under ~150 lines of meaningful changes
- Changes are straightforward: typo fixes, config tweaks, log/comment changes,
  dependency bumps, small bug fixes with obvious cause and fix
- Does not modify the wire protocol (`.proto` files, serialization, `BitWriter`/`BitReader`)
- Does not alter the peer state machine or authentication/handshake flow
- Does not change state synchronization (snapshot ring buffer, `SnapshotBoard`, sliding window, delta encoding, AoI)
- Does not modify worker-shard isolation boundaries or inter-worker communication
- Does not touch hardening layers (rate limiting, admission control, abuse detection)
- Does not change the tick loop, timing, or execution ordering
- Does not modify DI container registration or service lifetime scoping
- Does not alter ENet transport configuration, channel semantics, or native interop
- Does not change metrics collection, Prometheus export, or observability pipeline
- Does not modify Docker build, deployment, or infrastructure configuration in ways that affect runtime behavior

A PR is COMPLEX when ANY of the following are true:
- Touches the wire protocol or serialization codegen (`protoc-gen-bitwise`, quantization)
- Modifies the peer state machine, handshake, or auth chain validation
- Changes state synchronization, snapshot lifecycle, or delta encoding
- Alters worker-shard boundaries or adds cross-worker communication
- Modifies hardening/admission policies or adds new protection layers
- Changes the tick loop structure, timing, or processing order
- Affects DI registration, service lifetimes, or startup ordering
- Introduces new dependencies or changes native interop
- Modifies ENet transport behavior or channel semantics
- Risk of silent data corruption, peer-state desync, or connection leaks is non-trivial
- Large diff (4+ files or significant logic changes)

--- STEP 7 — QA assessment ---
Determine whether QA (manual testing with a client) is needed for this PR.

QA_REQUIRED: NO when ALL of the following are true:
- Changes are limited to CI/CD workflows (.github/), scripts, documentation,
  config files, tooling, benchmarks, or other non-runtime code
- No wire-protocol or serialization changes that could affect client compatibility
- No changes to code that runs in the server process at runtime
- Unit/integration tests adequately cover the change

QA_REQUIRED: YES when ANY of the following are true:
- Changes could affect what the client sees or experiences (state sync, movement, emotes, chat)
- Modifies runtime server code that processes peer connections, game state, or network I/O
- Touches authentication, handshake, peer state machine, or teleport flows
- Changes hardening behavior that could reject legitimate clients
- Alters protocol serialization or message formats
- Modifies snapshot/delta encoding that could cause client-side desyncs

--- STEP 8 — Non-blocking warnings ---
Emit these as warnings in the summary comment. They do NOT cause a FAIL on their own.

- **Proto File Modified** — If any `.proto` file under `src/Protocol/` appears in the changed files:
  > ⚠️ **Protocol file modified**. Verify client-side compatibility and that `protoc-gen-bitwise` codegen was re-run.

- **Hardening Config Changed** — If hardening options or admission policies are modified:
  > ⚠️ **Hardening configuration changed**. Verify that legitimate clients are not affected under normal and peak load.

- **Docker/Deploy Modified** — If `Dockerfile*`, `docker-compose*`, or deploy workflows are changed:
  > ⚠️ **Deployment configuration modified**. Verify the change works in both local debug and production Docker contexts.

--- STEP 9 — Verdict ---
At the very end of your output, emit exactly these four lines (order matters — downstream automation parses them):
REVIEW_RESULT: PASS ✅  (or FAIL ❌)
COMPLEXITY: SIMPLE  (or COMPLEX)
COMPLEXITY_REASON: <one sentence citing which subsystem(s) the diff touches>
QA_REQUIRED: YES  (or NO)
