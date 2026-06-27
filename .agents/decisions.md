# Agent Decisions

Record durable repo decisions here. Do not use this as a chat log. Each entry should make
sense without conversation history and should name superseded guidance when relevant.

Keep this file to what is currently in force or still open. When a decision is
closed - superseded, or settled and retained only as the rationale for a rule that
now lives in its canonical home elsewhere - move it verbatim, in that same change,
to an archive under `docs/history/` (for example `docs/history/decisions-archive.md`);
never summarize or drop wording, the exact text is the record. Keep a single
pointer to the archive at the top of this file, not a stub per entry. The archive
is the provenance log; this file is what is in force or still open.

## Decision lifecycle

A decision moves through these states:

- **Open** - a finding has been assessed but not yet acted on. It lives in the
  `## Open Decisions` queue below, with the verified evidence, the options, and a
  standing recommendation. The process is unchanged until it is adopted; an agent
  records it rather than implementing on the spot.
- **Active** - a decision that is in force now.
- **Adopted YYYY-MM-DD** - an Open finding that has been acted on: its rule now
  lives in its canonical home (a procedure, template, or invariant). Note where the
  rule landed; the finding is retained in place as the rationale that led to it,
  until it is archived.
- **Superseded** - replaced by a later decision; name the replacement.

When an entry becomes purely historical rationale - Adopted or Superseded, with the
live rule now owned elsewhere - archive it per the rule above: move it verbatim to
`docs/history/`, do not leave a stub.

## Decisions

No durable decisions recorded yet.

## Open Decisions (deferred - not yet adopted)

### 2026-06-26 - Adversarial review findings (PwshTokenCompressor.psm1)

A full adversarial review of the module surfaced the findings below. All 19 Pester
tests pass, but several pass by luck or mask the defects. Findings are verified at
runtime where marked. None acted on yet; recorded here rather than fixed on the spot.

Recommendation: fix High items 1-3 and Medium item 4 before relying on the module;
each as its own commit per the one-fix-per-commit rule.

**High**

1. **Arg parser breaks on dash-prefixed values / negative numbers** -
   `Invoke-PtcBoundCommand` (psm1:69-85). Any token starting with `-` is treated as
   a flag name; the next token is its value unless it also starts with `-`. Verified:
   `ptk read f -MaxLines -5` -> "A parameter cannot be found that matches parameter
   name '5'"; `ptk grep "-Force" ./src` -> cannot search any pattern starting with
   `-`. Values like `-1`, `-v2.0`, regex `-\d+`, or path `-foo` are unreachable.
   Options: pass through to the real binder; or treat a token as a flag only if it
   matches `^-[A-Za-z]` (not `^-\d`) and add a `--` end-of-options sentinel.

2. **Stale `LASTEXITCODE` reported as failure** - `Invoke-PtcRun` ScriptBlock path +
   `Get-PtcLastExitCode` (psm1:50-54, 891-900). Reads global `$LASTEXITCODE`, which
   only reflects the last native exe, not a scriptblock. Verified: with leftover
   `LASTEXITCODE=7`, `ptk run { [pscustomobject]@{Name='ok'} }` appends a false
   `[exit] 7`. Options: reset `$global:LASTEXITCODE = 0` before invoking; or only
   surface an exit code when a native command actually ran.

3. **Required test fixture is git-ignored -> fresh clone fails** - `.gitignore:3`
   (`*.log`) vs Tests.ps1:89. `tests/fixtures/SmallLog.err.log` is read by a test but
   untracked (`git ls-files` confirms absent); the suite is green only because the
   file exists locally. Clean checkout -> CI failure. Options: `git add -f` the
   fixture; or rename it off the `*.log` pattern (e.g. `SmallLog.errlog`).

**Medium**

4. **Markdown code blocks counted double** - `Compress-PtcMarkdownSummary`
   (psm1:409). The fence regex matches both opening and closing ```` ``` ````.
   Verified: a single ```` ```powershell ```` block reports `2 code blocks` and
   `code: plain=1, powershell=1` (closing fence becomes a phantom `plain` block).
   Fix: count fences pairwise; take only odd-indexed openers.

5. **Comment stripping corrupts code via string/URL false positives** -
   `Remove-PtcGenericComments` (psm1:280). `^(//|#)` drops any line starting with
   those tokens even inside multi-line strings; `"""`/`=begin`/`=end` matching is
   language-agnostic and fires on non-Ruby/Python files; no inline or nested-block
   awareness. The `minimal` level is meant to be near-lossless but is not. The
   `Use-PtcNeverWorse` guard checks length, not correctness, so corrupted-but-shorter
   output is returned.

6. **`Use-PtcNeverWorse` guards size, not fidelity** - (psm1:214-225). Compares only
   `.Length`; output that silently drops a body/line counts as "better." Consider
   renaming to `Use-PtcShorter` and documenting that fidelity is not guaranteed above
   `none`.

**Low / quality**

7. **`Compress-PtcCodeAggressive` regex misses declarations** - (psm1:393-396).
   Anchored at `^` after trim, so indented members, C# methods (no `function`
   keyword), decorated/attributed lines, and `export default` are dropped. README
   oversells "keep signatures" for C#/Java.

8. **`$args` automatic variable shadowed** - `Invoke-PtcList` (psm1:723). Harmless
   now (splat only) but a smell; rename to `$gciArgs`.

9. **`Invoke-PtcRun` string path interpolates `$temp` into generated script** -
   (psm1:903-919). GUID names are safe, but a temp path containing `'` would break
   the generated script. Prefer passing the path via env var / `-Args`.

10. **`Format-PtcTable` magic number 50** - (psm1:155,168). Column width hard-capped
    at a literal 50, unrelated to `$Width`/`$DefaultWidth`; inconsistent truncation.
    Extract a constant.

11. **Test weaknesses** - Tests.ps1. Line 66 (`Should -Match '\+'`) is too loose;
    no test covers the arg parser with `-`-prefixed/negative values or the markdown
    double-count - exactly the broken paths, so green status overstates correctness.
