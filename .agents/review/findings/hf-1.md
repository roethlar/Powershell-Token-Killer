# hf-1: ptk_output offset schema emits string minimum/maximum, violating JSON Schema draft 2020-12

**Severity**: MAJOR
**Status**: Fixed, pending external review (branch `fix/ptk-output-schema-draft2020`, head `3505edd`, base `019a28d`)
**Source**: operator-reported production defect 2026-07-18 (install on a second machine)
**File**: `server/PtkMcpServer/Tools/OutputTool.cs:32`

## Evidence

The `offset` parameter of `ptk_output` carried
`[Range(typeof(long), "0", "9223372036854775807")]`. The
`(Type, string, string)` constructor makes the MCP schema generator emit
`"minimum"` and `"maximum"` as JSON **strings** in the tool's
`input_schema`. JSON Schema draft 2020-12 requires numeric operands for
`minimum`/`maximum`; strict MCP clients validate every tool schema at
session start and reject the entire toolset:

> API Error: 400 tools.37.custom.input_schema: JSON schema is invalid.
> It must match JSON Schema draft 2020-12.

## Predicted observable failure

Any strict-validating MCP client refuses the server's tool registration
wholesale — not just `ptk_output` — making the server unusable on that
client. Reproduced by the operator on a clean install; confirmed
deterministic (schema generation is static).

## What

Use the numeric `Range` constructor. The chosen maximum is
`9007199254740991` (2^53−1), the largest exactly-representable integral
double, since JSON interoperable numeric range is double-bounded; real
offsets are bounded far below it by `OutputStore` size caps. Add a
conformance guard over every generated tool schema.

## Scope of fix

One attribute in `OutputTool.cs` plus a new conformance test class. The
public contract digest chain is mechanically recomputed (the public tool
contract's bytes changed): `public-tool-contract.json` artifact sha +
`ptk.public-contract/1` domain digest → `public_contract_sha256` in
`package-manifest.example.json` → `artifact_sha256` /
`digests.package_manifest.example_digest` in `contract.json` →
`ContractSha256` constant in `McpResilienceR0ContractTests.cs`. Manifest
byte length is unchanged (2233), so `example_raw_utf8_bytes` is
untouched. No behavioral change to offset handling: the runtime cap on
`offset` was already enforced by `OutputStore`, and 2^53−1 exceeds every
reachable store size.

## Resolution

Commit `3505edd` on `fix/ptk-output-schema-draft2020`:

- `OutputTool.cs` — `[Range(0d, 9007199254740991d)]` with a comment
  forbidding the string-operand constructor and recording the 2^53−1
  rationale.
- `ToolSchemaConformanceTests.cs` (new) — generates the ACTUAL input
  schema for every `[McpServerTool]` method through the production SDK
  factory (`McpServerTool.Create`, DI parameter types registered by
  convention so they are excluded exactly as in the real host), strict-
  parses the JSON, and walks it with a schema-position-aware draft
  2020-12 structural validator (numeric operand keywords must be JSON
  numbers within ±(2^53−1); integer, string, `type`, `required`,
  subschema-map/array keyword typing; draft 2020-12 single-schema
  `items`). A self-test runs the legacy string-operand attribute through
  the same factory and asserts the validator rejects it, proving the
  guard is not tautological. Attribute-level `RangeAttribute` pins are
  retained as a fast early signal.
- Digest chain recomputed as described in Scope.

## Guard proof

Written (`ToolSchemaConformanceTests`, 39 tests). The validator is
proven non-tautological by the legacy-attribute self-test; it covers all
five generated tool schemas, so bad bounds from any schema source (not
just `RangeAttribute`) are caught. The existing
`McpResilienceR0ContractTests` digest-closure tests pin the recomputed
chain end-to-end. Full suite green: 1575/1575.

## Reviewer comments

Round 1 (head `3505edd`): codex / gpt-5 / high / standard, base
`019a28d`, guard_confirmed **false**, verdict **rejected**. Blocker: the
guard only reflected over `RangeAttribute` operands and never validated
a generated schema — implementation-coupled and weaker than this record
claimed. Non-blocking confirmations from the same round: the production
fix itself verified correct (independent MCP 1.4 schema-generation probe:
numeric bounds at head, string bounds with the old attribute; draft
2020-12 meta-schema validation passed all five head tool schemas); the
2^53−1 maximum verified non-narrowing (`OutputStore` 8 MiB artifact cap);
digest chain independently recomputed clean
(`4467db27…`/`817f00e5…`/`5699d8fc…`/`2e923f91…`/`cd6aff30…`, manifest
2233 bytes); no unrelated hunks, no remaining string-operand `Range`
usages. Round 2 addresses the blocker by replacing the reflection-only
guard with generated-schema validation (see Resolution); re-review
dispatched at the new head.
