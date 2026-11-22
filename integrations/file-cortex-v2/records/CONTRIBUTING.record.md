# Contributing Tenets

## Admission standard

A tenet belongs in this library only when it:

- applies across projects or a recurring class of work;
- expresses a durable decision rule rather than a temporary fact;
- improves correctness, security, efficiency, recoverability, or maintainability;
- includes enough boundary information to prevent unsafe over-application;
- contains no project names, task IDs, credentials, private data, active-state claims, fixed endpoints, package hashes, or repository-specific commands;
- is not already represented by an existing canonical tenet.

Exclude isolated product behavior, uncommon platform trivia, hypothetical edge cases without material risk, and instructions that merely restate a tool's help text.

## Proposal shape

Propose new material using:

```text
Title:
General situation:
Decision rule:
Required behavior:
Important boundary:
Minimum verification:
Origin: user-derived | agent-derived | combined
Related tenet IDs:
```

Do not include private reasoning. Preserve public conclusions, evidence, failed approaches, caveats, and reusable lessons.

## Curation process

1. Remove project-specific context and transient state.
2. Search titles, rules, and related IDs for an existing equivalent.
3. Compare proposals by `decision + protected property + applicability + boundary`.
4. Merge different wording when all four are equivalent.
5. If the decision is shared but a specialized boundary differs, keep one general owner plus one narrowly stated specialization delta.
6. Keep separate tenets when superficially similar advice protects different ownership, security, compatibility, or failure properties.
7. Place the rule at the highest broadly valid layer and cross-reference it elsewhere.
8. Assign a stable ID using the canonical file's prefix.
9. Review for conflicts with user authority, project instructions, and established public contracts.
10. Prefer a small revision to an existing tenet over adding a near-duplicate.

When several agents contribute, collect proposals independently and let one curator perform the semantic merge. Do not preserve duplicate rules merely to retain contributor attribution.

## Taxonomy

- `core/`: universal engineering rules.
- `disciplines/`: broad engineering subjects reusable across tools and platforms.
- `workflows/`: ordered application of core and discipline tenets to recurring work.
- `specializations/tools/`: tool-specific deltas.
- `specializations/protocols/`: protocol-specific deltas.
- `specializations/interfaces/`: interface-class deltas.
- `specializations/platforms/`: operating-system or application-platform deltas.
- `specializations/architectures/`: processor or runtime-architecture deltas.
- `specializations/environments/`: execution or development-environment deltas.
- `specializations/ecosystems/`: external product-ecosystem deltas.
- `specializations/languages/`: programming-language deltas.

Use the highest broadly valid owner: `core` before `disciplines`, `disciplines` before `workflows`, and general layers before specializations. Taxonomy describes applicability; it does not create separate copies of the same rule.

The taxonomy is open. Add a new specialization kind only when accepted material does not fit an existing kind. Do not create empty folders or a category for one transient fact.

## Review standard

Before accepting a change, verify that the guidance is concise, actionable, broadly reusable, correctly scoped, and not dangerously absolute. Confirm it remains useful across at least two unrelated projects or a recognized broad discipline, survives changes in vendor and framework, and is not merely a restatement of documentation. A tenet guides work but never authorizes source edits, builds, deployments, destructive actions, credentials, or live-system mutation.
