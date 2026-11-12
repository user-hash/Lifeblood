# Owner-scoped content evidence

`lifeblood_file_scope` captures or verifies SHA-256 receipts for an explicit file
set in the loaded workspace. It performs no analysis, source writes, generation
changes, persistent scope registration or semantic-base allocation. Unrelated
file edits and publication changes do not invalidate a receipt. `ownerId` labels
caller intent; it grants no ownership, lock, reservation or authorization.

## Capture and verify

```json
{"action":"capture","ownerId":"piano-calibration","filePaths":["Assets/Piano.cs","Assets/PianoTests.cs"]}
```

Use the actual returned receipt, not the illustrative placeholder hash above.
Verify checks every receipt path, rejects owner/workspace/schema mismatches, and
returns `matched` or `conflict` with exact expected/actual file evidence. A conflict
is an MCP tool error. Timestamp-preserving changes are detected by their bytes.

## Guarded compile-check

Pass the receipt as `expectedFileScope` alongside `filePath` or `filePaths`.
Every compile target must appear in the receipt. New file-scope requests and
guarded compile-check validate root arguments strictly even under legacy JSON
compatibility, so malformed targets or filters cannot silently disappear.
Extra receipt paths are allowed
as explicit content guards, for example a related test or descriptor. A missing
compile target cannot be checked even when its absence expectation matches.

```javascript
const compileArguments = {
  filePaths: ["Assets/Piano.cs", "Assets/PianoTests.cs"],
  expectedFileScope: captureResult.receipt
};
```

The server validates all scoped bytes before any refresh or compilation. A
conflict returns `failure:file-scope-precondition`, expected/actual rows,
`compilationStarted:false` and `autoRefreshed:false`. Invalid receipts fail closed
even under legacy JSON compatibility. On success, compilation consumes the exact
captured bytes that matched the hashes, with BOM-aware decoding. It does not read
the files a second time after validation. Output `preconditions` identifies the
scope, checked count, actual publication and `hashVerifiedCapturedBytes` authority.

Guarded checks default `staleRefresh` to false and reject explicit true. This
prevents precondition admission from silently replacing the compilation baseline.
Legacy calls without preconditions retain their existing refresh default and
response shape. File batches still check each file individually against the
retained compilation; they do not jointly install all changed files into a new
compilation or qualify an entire build.

For exact semantic-baseline admission, independently supply `expectedSnapshotId`,
`expectedAnalysisGeneration`, or both. Comparison, file capture, compilation and
response identity run inside the same session writer gate. Publication mismatch
returns the existing structured retry guidance before reading source or doing
work. Historical selection is not available for compile-check. Snapshot IDs are
the cross-daemon identity; generation alone is process-local.

## Conditional file application

On Windows, `lifeblood apply-file` consumes the same receipt and a staged raw-byte
replacement for one existing target. Save the capture response's `receipt` object
as `before.json`, then run the explicit D: CLI installation:

The command opens every receipt file in deterministic order with
[`FileShare.None`](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare),
checks hashes under those held handles, and writes through the same target handle.
Ordinary Windows writes and atomic rename/replacement cannot intervene between
the comparison and application. Busy files fail before writing. Hash conflicts
return exit 2 and an observed receipt; do not retry with that receipt as permission
to overwrite another owner's content. Success returns exit 0 and an updated
`receipt`, which can be passed directly to guarded compile-check. Identical bytes
return `unchanged` without writing. No analysis or service connection is opened.

The target must be covered and all guard expectations must name existing files;
creation, deletion and multi-file application are outside this command's contract.
The same scope/replacement byte bounds apply. Arguments and JSON are strict, with
missing, unknown, duplicate and null-required fields rejected. Exit 1 indicates
invalid/unsupported input, exit 3 unavailable files, and exit 130 cancellation
before commit. Cancellation is not honored halfway through the protected write.

This is conditional application, not a crash-atomic transaction. On a caught I/O
write failure, restoration runs before locks are released. Exit 4 reports
`writeState:originalRestored` or `unknown`; inspect unknown content before further
work. Hard process termination, power loss, preexisting writable memory mappings
and nonstandard filesystem behavior are not covered. The command refuses other
operating systems rather than claim equivalent mandatory sharing semantics.
The receipt is evidence at commit, not a lasting reservation after handles close.

## Concurrent-work contract

For multiple agents using the same workspace:

1. Join its shared MCP service and inspect live capabilities. Reuse a compatible
   publication, recording its snapshot, profiles and any freshness limits. A CLI
   analysis is a separate batch job rather than a connection to that publication.
2. Agree on the intended source scope. Capture its pre-edit receipt before
   preparing a replacement. On supported Windows existing-file writes, pass
   that receipt and staged bytes to `apply-file` under the consumer's bounded
   source admission; release that admission in `finally` before returning or
   allowing an Editor reload. Ordinary unrelated work needs no standing hold.
3. On an overlapping-byte conflict, read and reconcile the other change, then
   deliberately recapture and restage. A new receipt alone never authorizes
   overwriting content. For other writers, enforce their own conditional-write
   contract; separate verify-then-write is insufficient.
4. Compile using the successful writer's post-edit receipt, or capture the
   intended post-edit bytes if another supported writer applied them. Supply
   the publication precondition when the semantic baseline must be exact.
   Unrelated source changes do not invalidate this content guard.
5. Refresh the shared publication after coordinated cross-file/API edits before
   checking dependent files. A compile batch checks individual file overlays
   against retained dependencies; it is not a joint multi-file candidate. For
   example, a newly added member in file A remains unavailable to file B's
   overlay until ordinary analysis incorporates A. Read the refresh receipt
   and re-evaluate new snapshot identity before retrying pinned work.
   When a descriptor change requires full fallback, schedule that heavy work
   outside consumer latency/audio timing qualification where possible. Record
   any overlap as a measurement confound; source-byte stability alone does not
   make concurrent workstation workloads comparable.

This procedure reduces avoidable conflicts while preserving content and
publication guards. It does not make overlapping edits automatically mergeable
or provide dependency-scoped freshness. Select only needed define profiles;
historical graph snapshots do not carry live Roslyn services. One semantic base
does not imply one process, CPU core or GC heap. A tool timeout alone does not
prove an asynchronous operation stopped: follow its existing handle/receipt
before submitting a replacement or performing maintenance.

A typical diagnostic workflow captures the intended post-edit content receipt,
then compiles with that expectation. Another agent editing unrelated files does
not break the content guard. Changes to an overlapping file reject before
compilation. Receipts may also be captured before editing to check whether a
baseline has changed, but a separate verify followed by an external write has a
race. Use `apply-file` for its supported existing-file Windows contract; other
editors/writers must enforce their own conditional write.
Do not silently refresh a failed expectation and then overwrite the new content.

Hash verification guarantees check-and-use of request-local bytes, not an atomic
filesystem snapshot or a lock on other editors. Files are observed individually.
It also does not prove dependency, assembly-definition, reference, rule or profile
freshness: `semanticInputsVerified:false` remains explicit. Exact-publication
preconditions pin the retained baseline but do not equate it with current disk.
Use the existing canonical snapshot drift check when that stronger evidence is
needed. A scoped receipt never substitutes for Unity/Burst/native acceptance.

Unity bridge equivalents are `lifeblood_file_scope(receiptJson:...)` and
`lifeblood_compile_check(expectedFileScopeJson:...)`: the outer adapter parses
those JSON strings into the same native object contracts and retains polling
and error propagation. Publication precondition fields keep their native names.
