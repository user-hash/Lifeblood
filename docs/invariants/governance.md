# Governance Invariants

Doc / repo discipline: pinned counts in `docs/STATUS.md`, complete
CHANGELOG link references, and test-pattern rules.

- **INV-DOCS-001. Doc numbers match the repository.** `docs/STATUS.md` declares port and tool counts in HTML comments (`<!-- portCount: N -->`, `<!-- toolCount: N -->`). The HTML comment is the single source of truth; `DocsTests` parses it and asserts the number matches the live count of `public interface I*` declarations under `src/Lifeblood.Application/Ports` (ports) and `Name = "lifeblood_*"` literals in `ToolRegistry.cs` (tools). Editing the count in one place and not the other fails the ratchet.

- **INV-CHANGELOG-001. Every version heading has a link reference.** `CHANGELOG.md` must contain a `[X.Y.Z]: https://github.com/.../compare/...` link reference for every `## [X.Y.Z]` heading. Ratchet-tested by `DocsTests.Changelog_EveryHeadingHasLinkReference`. Closes the drift class where v0.6.0 shipped with stale bottom-of-file link refs.

- **INV-TESTDISC-001. Tests never silently early-return on precondition failure.** The `TryAnalyze(out ...) ⇒ bool` + `if (!TryAnalyze(...)) return;` pattern is forbidden — it hides both legitimate skips and real failures as silent passes. Missing preconditions (golden repo not restored) turn into `Skip.IfNot(condition, reason)` via `Xunit.SkippableFact`; broken-but-present conditions (graph has zero symbols) turn into loud `Assert.True` / `Assert.Fail`. Canonical example: `WriteSideIntegrationTests.cs`. Grep for `if (!Try*` under `tests/` must return zero hits.
