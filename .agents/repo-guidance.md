# Repo-Specific Guidance
<!-- Extends AGENTS.md; never overrides it. Rules and pointers only — state
     lives in .agents/state.md. -->

## Mission Detail

PwshTokenCompressor (invoked as `ptk`) is a PowerShell-first token-compression
tool for agent workflows: it captures PowerShell objects before they are
formatted to text, summarizes them by type and selected properties, and
renders compact output for LLM tool use. It is a structured-output compressor,
not a Unix-command wrapper (see `README.md`).

The owner treats this as a personal/team tool complementing a separate
`headroom` proof-of-concept for Windows/PowerShell work, not an org-wide
product. The build trigger for larger architectural changes is measured
benefit on real daily usage, not anticipated need. Two such changes are
currently deferred pending that evidence — see the Open Decisions in
`.agents/decisions.md` rather than re-deriving this framing.

## Reading Order

1. `AGENTS.md`
2. `.agents/repo-guidance.md` (this file)
3. `.agents/state.md`
4. `.agents/decisions.md`
5. `README.md`
6. `src/PwshTokenCompressor.psm1` and `tests/PwshTokenCompressor.Tests.ps1`

## Verification

Confirmed automated verification command (re-run 2026-07-02, 31/31 passed):

```
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
```

Requires the Pester module (5.8.0 confirmed present in this environment). No
CI is configured in this repo (no provider-executable workflow files found);
verification is local-only. See `.agents/repo-map.json` for the machine-
readable record.

## Remotes & Sync

Two remotes are configured (`git remote -v`):

- `origin` — `https://github.com/AlsoBeltrix/PowerShell-Token-Killer.git`
- `personal` — `https://github.com/roethlar/-PowerShell-Token-Killer.git`

`master` tracks `origin/master`. Push policy stays in `.agents/push-policy.md`,
not here.

## Earned Practices

None recorded yet beyond the portable Git Safety rules in `AGENTS.md`. Add an
entry here only once a real, citable incident in this repo earns a
repo-specific rule.
