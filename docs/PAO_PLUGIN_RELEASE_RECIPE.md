# Plugin Release-build recipe contract 1

Source-only preparation, coordinated with the Cerebral owner on 2026-10-06.
No installed recipe, service, model, security policy, Cerebral source, starter
store, or installed guidance has been changed. On 2026-10-07 the source-only
Cerebral guide pack advanced to revision 5. This is not plugin acceptance.

## Current and candidate contract

Installed Cortex `1.2026.09.29.22.18.A` advertises `do` with `rec`, optional
bounded JSON-string `vars`, separate `cwd`, `rid`, `detail`, and `output`.
The installed recipe list contains only `cerebral-build-release`,
`cerebral-package-installer`, and `cerebral-publish-kit-x`, all PowerShell actions.

The current `OneActionRecipeDefinition` DTO already includes `Dotnet` (3), but
the installed engine rejects that recipe action. The source candidate enables
only the closed profile below. No new MCP field or registry schema is needed.

- Registry envelope: `schemaVersion=1`, existing `recipes.v1.json`.
- New named definition: `plugin-build-release`, `definitionVersion=1`.
- Action: `Dotnet` (numeric 3); tool: registered/probe-verified `dotnet`.
- Variable: exactly one required `project`, `RelativePath` (numeric 3),
  maximum 1024 UTF-8 bytes, no default or extra variables.
- Mode: User (0). Registered tool must use Caller working-directory policy.
- `cwd`: mandatory separate fully qualified existing admitted directory.
  No ambient directory, fixed/guessed workspace, or arbitrary absolute operand.
- Target: existing `.csproj`, canonicalized beneath `cwd`. Rooted paths,
  traversal, ADS, unsafe Windows path syntax, and file/directory reparse
  components reject before dispatch. The canonical path budget is 240 UTF-16
  characters. Existing path normalization, no-follow and PrepareDotnet checks
  remain in use; this recipe creates no grants.
- Exact argv: `build`, canonical project, `--configuration`, `Release`,
  `--disable-build-servers`, `--verbosity`, `minimal`.
- Deadline: explicit 120 seconds or a narrower definition; never above the
  registered tool deadline. Expected exit: exactly zero.
- Capture: at most 16384 UTF-8 bytes **per stream**, with honest truncation.
  Existing receipt-first projection remains bounded to 4 KiB and four concise,
  privacy-filtered diagnostics. Full retained bounded output uses `job_log`.

The complete raw definition is
`integrations/pao-recipes/plugin-build-release.v1.json`. Its numeric enums match
the existing owner-registration DTO parser; persisted registry enums remain
the registry's existing named-string format. Schema version is not the recipe
definition revision, and neither is the Cortex release version.

This enables no other Dotnet recipe, configurable switches, publish, install,
activation, multi-step action, PowerShell wrapper, shell command, or grant change.
Ordinary `dotnet build` can perform its standard dependency restore and execute
project-defined build targets: owned/admitted source and applicable permissions
are still required. This is not an arbitrary-project sandbox or an offline-only
build guarantee.

## Exact registration seam — not executed

After a compatible release and separately coordinated owner authorization:

1. Verify the installed recipe capability, registered dotnet policy/probe,
   timeout and output bounds. Do not relax the tool definition to fit a recipe.
2. Use the product-local CLI `pao.exe recipe add --stdin`. Its bounded stdin is
   the exact UTF-8 definition file, not a shell command or a copied invocation.
   This enters `AgentOpsEngine.RecipeAdmin`, then
   `OneActionRecipeRegistry.Register`; public MCP exposes discovery/execution,
   not recipe registration.
3. Inspect `recipe_ls` after registration. Existing same/lower definition
   versions reject; don't remove or overwrite an unrelated definition.

The existing installed runtime must not receive this definition: it will reject
Dotnet recipes with `precondition`. Contract availability, not equal product
versions, determines compatibility. No recipe capability is claimed installed.

## Cerebral adapter and one-step execution

Cerebral owns recipe choice and variables. Its authoritative BuildSummary gives
the exact absolute project, exact component working directory, registered
executable ID `dotnet`, and expected Release manifest. Derive a relative project
from that pair and verify resolving it beneath the exact directory yields the
same canonical project. Never ask the model to guess either path.

The existing call shape remains:

```json
{
  "op": "do",
  "rec": "plugin-build-release",
  "cwd": "<exact admitted component directory from BuildSummary>",
  "vars": "{\"project\":\"<validated relative project>\"}",
  "output": "agent"
}
```

Use a fresh owner operation identity for a new build after a source repair.
Reuse the same RID only to reconcile/resubmit that exact original request; a
completed effect is not a reason to build again. A receipt reference identifies
retained evidence, not proof of compilation success. Require zero exit and
separate expected-manifest validation before proceeding to stage/activation.
An old manifest's existence alone never proves the current build succeeded.

On `external_failed`, retain JID/exit, concise compiler codes/locations and
truncation; read the exact source needed to repair. On timeout, partial result
or uncertainty, retain the receipt, inspect bounded `job_log`, reconcile state,
then decide whether a fresh build is safe. There is no automatic replay.
For omitted/truncated diagnostics expose log availability and the exact JID;
the continuation cannot recover bytes beyond the retained capture cap.

## Portable guidance and owner boundaries

The existing deterministic behavior-pack source now carries
`Knowledge.Cerebral.Guide.BoundedWork` and the revised PluginAuthoring guide.
These are on-demand reference records, not a scheduler or a new delegation
schema. They remain uninstalled and unimported. The application already copies
the module through its existing content glob; no new package loader is required.

- First Mate keeps the bounded plan/acceptance contract; helpers receive only
  the current small step. A recipe hides argv construction, not failures,
  receipts, or authority. It performs one action, never a hidden workflow.
- Discover registered recipes when useful. Use this recipe only when the
  installed contract and definition actually exist; otherwise preserve the
  current typed build path. Never infer registration from this document.
- Freeze scope, routing, grants, limits, and selected evidence in the owning
  host contract. References without resolved bodies are not loaded context.
  Preserve missing/stale/unavailable dispositions and exact revisions; never
  silently substitute a newer record or transfer parent authority.
- Honor the host's canonical source-generation attempt gate. An admission
  marker can survive rejection, cancellation, or an unknown outcome without
  proving the compiler ran. Reconcile before retry; repeated unchanged edits
  or identical errors require replanning or a bounded blocker.
- Build, verification, presentation, owner approval, native load, controls,
  persistence, and visual acceptance are separate evidence. Owner-review load
  failure uses only an advertised owner-approved repair seam and separate
  approval of the replacement. Accepted repair does not prove completion.
- Compiler/process execution can invalidate retained source-file hashes.
  After an existing-file rejection, no write occurred: call the owning exact
  `file.read` again for that destination, then retry once with
  `collision=replace` and the exact returned `expectedSha256`. Do not repeat
  `collision=fail` or obey stale "do not reread" advice. For native PAO edits,
  use the fresh full-file preimage SHA returned by the owning typed read.
- CS0117, CS7036, CS1525, CS1002, CS1001 and CS0236 from Cerebral's reported
  Desk Lantern trial are source/API errors, not recipe-contract or model-health
  failures. Only bounded generalized recovery belongs in the shared guide;
  the private trial/path/output history does not belong in the starter pack.

The error codes and source-generation/review-repair behavior above were reported by the Cerebral owner, not
independently reproduced against its active trial. This source checkpoint uses
isolated fixtures and the existing receipt/error projection tests. Live plugin
acceptance, package adoption and persistent registration remain separate gates.
