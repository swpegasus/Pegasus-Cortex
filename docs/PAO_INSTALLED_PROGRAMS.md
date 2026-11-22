# Windows program-registration inventory

The `installed_program_list` and `installed_program_read` operations observe
standard local Windows Uninstall registrations. They do not verify installed
files or product health, run an uninstaller, repair products, install software,
expand environment values or probe installation directories.

Both use the existing ordinary owner Host and admitted registry binding. Root,
System, UAC, alternate-user and remote-registry contexts are refused. Reads do
not create durable jobs or replay an unrelated operation merely because its RID
matches. Normal shutdown admission and cancellation still apply.

Pass a closed JSON string in `text`, or use the literal CLI family with stdin:

```text
pao installed_program list --stdin
{"version":1,"source":"machine64","limit":32}

pao installed_program read --stdin
{"version":1,"source":"machine64","key":"exact-registration-key"}
```

The required source is exactly `machine32`, `machine64`, `owner32` or `owner64`.
Those select the corresponding HKLM or original owner's HKCU registry view at
`SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`. They are independent
registration views and may overlap; counts are not globally deduplicated.

List accepts `limit` (1–64, default32) and an optional returned typed `cursor`.
Continuation is bound to the source, root identity and observed generation.
Changes invalidate the continuation rather than constructing a mixed snapshot.
Read accepts one immediate child `key` and optional observed `generation`.
No arbitrary registry path or caller-selected value name is accepted.

Results contain source/key/generation, name/version/publisher/install-date
metadata, reported estimated size and fixed field-visibility issues. Missing,
unsupported, changed or oversized fields remain explicit. Commands, URLs, icon
paths, executable arguments, account credentials and install locations are not
queried. A registration with no display name is not silently discarded.

`visibility_partial` and receipt `partial` describe incomplete observation, not
an issued side effect or unknown mutation. A missing initial registration root
is distinct from access denial. Owner loss or cancellation discards the page;
no fallback to another account or registry view is attempted.

This source addition is separate from the prepared signed06.34 candidate.
Fixture and live qualification must be reported separately; declaration in a
catalog is not execution evidence. Install/uninstall administration remains a
separate capability, not implied by this read-only inventory.
