# gov-doc-1: .NET tool slice contradicts the one-`~/.ptk`-home commitment

**Severity**: LOW — internal plan contradiction; misleads a future implementer, no runtime impact
**Status**: In progress
**Branch**: master (direct commit per repo codex-loop precedent)
**Commit**: (filled after commit)

## Evidence
`.agents/plans/release-distribution.md` — the "One ptk home" commitment says
`~/.ptk` "on every platform, for every install method," and the
package-manager rule says deliver into `~/.ptk` or don't ship; slice 5 then
installs via `dotnet tool install -g --add-source`, which by .NET's design
places the payload under `~/.dotnet/tools` / the tool package store, not
`~/.ptk`.

## Predicted observable failure
An implementer executing slice 5 either stalls on the contradiction or, worse,
generalizes the tool-store layout, quietly forking the one-home rule the owner
explicitly set ("sometimes it's not in ~/.ptk" is exactly what was rejected).

## What
The one-home commitment was written for end-user installs; slice 5 is a
dev-only packaging rehearsal, but the plan never states the exemption, so the
two passages conflict as written.

## Approach
State the boundary explicitly in both places: the one-home commitment applies
to end-user install methods; the dev-only tool rehearsal is exempt on payload
location (it exists to rehearse packaging, is never distributed), while the
config home remains `~/.ptk` even for it (config resolution is
install-method-independent by design).

## Files changed
- `.agents/plans/release-distribution.md` — one-home commitment + slice 5 wording

## Guard proof
Prose-only change: no automatable guard. Manual check = the two passages read
consistently; reviewer re-grade on the corrected range is the verification.

## Coder dispute (if any)
None.

## Known gaps
None.

## Reviewer comments
(pending re-review)
