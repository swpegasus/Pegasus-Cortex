# Repository setup and product boundary

The maintained products use separate repositories under the GitHub account
`swpegasus`: `Pegasus-Cerebral` and `Pegasus-Cortex`. Shayne requested both
repositories start private. The unrelated `mnwinter` account is not a target.
Remote creation and source upload must be reported from their actual results;
these names alone do not prove either action completed.

Cerebral owns the Windows AI workspace, model orchestration, engine interfaces
and optional capability plugins. Cortex owns durable continuity, deterministic
storage/retrieval and local execution interfaces. They communicate through
versioned public interfaces. Keep product roots, lifecycle ownership and user
data distinct even when an installer coordinates prerequisites. Neither
repository should copy the other product's maintained implementation or add a
submodule just to couple releases. Coordinate a compatible contract change
across both owners when needed; existing installer requirements remain current
source behavior until their owner changes and verifies them.

This boundary follows the selected exact Cortex direction record
`project.cortex-cerebral-plan / knowledge / plan.r2.01-direction@1`, read by the
Cortex Efficiency Audit. It is a design decision, not installed acceptance.
The Cerebral plan continues to specify useful operation when optional memory
is unavailable. That target is distinct from current setup prerequisite checks.

## Licensing

No product-level license has been selected. Shayne wants broad personal and
commercial use with protection against cosmetic resale/rebranding; see
[the licensing intent](licensing-intent.md). Do not label the products Apache,
MIT or open source, or add a license grant, until Shayne confirms his intent.
Apache-2.0 permits commercial use without a revenue ceiling or royalty;
revenue-limited commercial terms would be a different licensing decision.
Upstream code, assets, models and tools retain their own terms and notices.
A future product license applies only to material its owner has rights to
license. Private repository visibility does not settle distribution rights.

The earlier Cortex record
`project.pegasus-cerebral / knowledge / cerebral-no-licensing-gate-20260913@1`
concerns runtime activation/paywall gates; it does not choose a source license.

## Source and local state

Keep one maintained source tree in each current product folder. Git ignore rules
exclude build/deploy outputs, temporary agent work, model weights, databases,
credentials and executable payloads. They retain only portable project binding
and explanatory README files from the local `.cortex` folder. Ignored files
remain on disk and are not migrated or deleted. Existing historical continuity,
installed data, keys, caches and recovery files remain owned by their products.

Do not bulk stage ignored output or use `git add -f` to include a local payload.
Review the actual candidate file list before the first source import. Templates
and deterministic fixtures need explicit review if a broad protection rule
excludes them. Keep permitted third-party notices alongside the source/assets
they describe. Acquisition and packaging manifests must describe any external
payload required for a clean build; do not claim a clone builds independently
until that path has actually been checked.

## Working together

Use the existing Coordinator and domain owners to integrate changes. Git does
not replace task ownership, approved execution budgets, tests or Cortex.
Local metadata setup does not authorize installed updates, service changes,
release signing, public source publication or deployment.

The product page is coordinated with the existing WebAuthority chat at
[WebAuthority](codex://threads/019fea87-86e0-7762-9c0e-d1673a74d810).
Its proposed existing destination is `https://cerebral.shaynewinter.com/`.
Keep private source links and unqualified downloads out of the public page;
label development status and distinguish shipped evidence from planned work.

## Proportional checks

For repository metadata, verify local root/branch, empty or intended index,
remote URLs once assigned, representative ignored and included paths, and
line-ending attributes. No product rebuild is required for these files.
Initial source import, remote push, site deployment and installed acceptance
are separate outcomes. Report unperformed steps explicitly.

## Distribution intent and current hold

GitHub is for maintained source code, README and the necessary repository/license
metadata. README explains additional dependencies and how a developer supplies
them. The product website will eventually provide a built ready-to-install
package. Do not upload build outputs, generated evidence, databases, models,
local agent state or dependency payloads into the source repositories.

Shayne explicitly said not to perform this distribution work yet. Both remote
repositories were created and visually verified Private, but remain empty. Local
roots have main branches and matching origin URLs; no commit, source upload or
push has been performed. The latest instruction holds source import/upload,
installer preparation and website publication for this distribution lane. It
does not cancel separately approved implementation or Cortex activation.

No certificates or trust/signing material belong in GitHub, including public
certificate files. Keep private keys, certificates, signing identities, local
trust configuration, credentials and signed distribution outputs external. The
README must explain the external setup without carrying those materials.
Ignore rules are only a file-selection aid; inspect the actual first-import
contents and embedded material before any future source publication.

Record the Cerebral/Cortex signed-library and connection validation requirement
without claiming it is implemented. Library provenance/signature verification
and live peer authentication are separate checks. Preserve actual installed
trust/identity safeguards and define deliberate replacement/rotation through an
authorized path. No trust changes, new certificate issuance, service changes or
connection-security implementation are authorized by this note. Owner review
must establish current source behavior and separately qualify runtime evidence.

The GitHub connector cannot currently read these private repositories, and the
local Git route has no usable noninteractive authentication. Their existence and
Private state were verified through Shayne's signed-in docked browser. No
credentials were requested, printed, created or reconfigured. Future upload
needs an authorized authenticated route and a coherent owner checkpoint.
