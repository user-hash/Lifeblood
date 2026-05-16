# Release Checklist

Eternal pre-tag gate. Every numbered step must be green before `git tag` runs. No skipping, no "fix it in CI later", no force-moving a published tag to recover from a missed step.

## Scope

This document governs every public release tagged `vX.Y.Z`. Pre-release tags (`vX.Y.Z-rc1`, helper tags) are out of scope. They must not match the `v[0-9]+.[0-9]+.[0-9]+` semver pattern that triggers the verification workflow.

Source of truth for related ratchets:

- `INV-CHANGELOG-001`. Every `## [X.Y.Z]` heading needs a matching `[X.Y.Z]:` link reference (`docs/invariants/governance.md`).
- `INV-DOCS-001`. `docs/STATUS.md` port, tool, and test count anchors match live repo state.
- `Lifeblood.Tests.DocsTests` owns both ratchets.

## Pre-tag steps

Run these in order on the release commit before tagging.

1. **All release commits are on the release branch.** No work-in-progress on disk. Confirm with `git status` (clean) and `git log --oneline <prev-tag>..HEAD` (every commit intended for the release is present, nothing extra).

2. **CHANGELOG entry exists and is honest.** `CHANGELOG.md` carries a `## [X.Y.Z] - YYYY-MM-DD` heading describing what landed since the previous tag. Section uses `Added`, `Changed`, `Fixed`, `Removed` per Keep a Changelog. Describes shipped work, not intent. Pending items belong in `docs/IMPROVEMENT_INBOX.md` or a plan, not in a release note.

3. **CHANGELOG link references are complete.** Two link refs at the bottom of `CHANGELOG.md` are mandatory:

   ```
   [Unreleased]: https://github.com/<org>/Lifeblood/compare/vX.Y.Z...HEAD
   [X.Y.Z]: https://github.com/<org>/Lifeblood/compare/v<prev>...vX.Y.Z
   ```

   `[Unreleased]` must point at the new tag, not the previous one. `[X.Y.Z]` must point at the comparison range from the previous tag.

4. **`docs/STATUS.md` anchors match live repo state.** Hidden HTML comments declare the live counts:

   ```
   <!-- portCount: N --><!-- testCount: N --><!-- toolCount: N -->
   ```

   Live truth: `public interface I*` declarations under `src/Lifeblood.Application/Ports` (ports), `Name = "lifeblood_*"` literals in `ToolRegistry.cs` (tools), xUnit-discovered test cases in `Lifeblood.Tests.dll` (tests). Visible prose in `docs/STATUS.md` must match the anchor.

5. **Full test suite green.**

   ```
   dotnet test Lifeblood.sln -c Release --no-restore
   ```

   Required: exit code 0, zero failures. Skips are only allowed when documented by an open `LB-INBOX-*` regression pin referenced in the test attribute. A red `dotnet test` blocks the release. The fix is to land another commit, not to skip the test or to amend the release note.

6. **Self-analyze green.**

   ```
   dotnet run --project src/Lifeblood.CLI -c Release -- analyze --project . --rules lifeblood
   ```

   Zero violations against the built-in `lifeblood` pack.

7. **Architectural maps current.** When the release touches public surface (new adapter, new MCP tool, new port, new invariant domain), the following are updated in the same commit set:

   - `README.md`. Language coverage line, ASCII diagrams, adapter table, docs table.
   - `docs/STATUS.md`. Component row and visible prose.
   - `docs/ADAPTERS.md`. Adapter section for new adapters.
   - `docs/ARCHITECTURE.md`. Left-side diagram, assembly table, capability section.
   - `docs/architecture.html`. Pills, left-column items, legend.
   - `CLAUDE.md`. Project tree and dependency rules for new projects.
   - `docs/invariants/<domain>.md`. New INVs in the appropriate domain file.
   - `docs/invariants/INDEX.md`. New domain row if a new file was created.

## Tag and push

Once every pre-tag step is green:

```
git tag vX.Y.Z <commit>
git push origin main
git push origin vX.Y.Z
```

Push `main` first, tag second. The tag-triggered verification workflow re-runs the test suite against a clean CI runner. Expected outcome is green.

## Package publish

Out of scope for this checklist. Package publish is a separate maintainer process that runs after the tag is in place. CI workflows do not publish.

## Recovery from a red release

A published tag pointing at a red commit (e.g. v0.7.6) stays in place. Force-moving a published tag is a destructive operation on shared history. Anyone who has already pulled or restored from the tag sees the rewrite.

The recovery is always the next patch:

1. Fix the cause on a new commit.
2. Walk this checklist again.
3. Tag `vX.Y.(Z+1)`.
4. Note the recovery in the new release's `Fixed` section so the history reads honestly.

## Why each step exists

- Step 3 closes the drift class that shipped v0.7.6 with three red CI workflows on origin. The `Changelog_EveryHeadingHasLinkReference` ratchet caught it locally. The operator pushed the tag anyway. The eternal mechanism is this checklist plus the existing ratchet. No new automation is required, only discipline applied before the tag command runs.
- Step 5 prevents the same operational gap from recurring through any other ratchet. A test-suite failure on the release commit is by definition a release blocker.
