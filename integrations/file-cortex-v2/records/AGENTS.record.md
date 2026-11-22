# AGENTS.md — Programming Tenets Library

## Purpose

Maintain broadly reusable engineering guidance for people and coding agents. This library is not a project-state store, task tracker, prompt archive, or substitute for a target project's own authority.

## Working rules

- Read `README.md` and `CONTRIBUTING.md` before editing.
- Admit only durable, recurring, project-independent decision rules.
- Remove project names, current paths, task IDs, endpoints, credentials, hashes, private data, active-state claims, and other transient context.
- Search for an existing equivalent before adding a tenet.
- Keep each rule in one canonical file with one stable ID; use cross-references instead of duplicate wording.
- Preserve materially different security, ownership, compatibility, and failure boundaries when merging similar advice.
- Treat the current catalog as coverage, not scope; the user's examples and existing folders are not an exhaustive subject list.
- Put universal rules in `core`, broad subjects in `disciplines`, ordered work in `workflows`, and only genuine deltas in `specializations`.
- Keep specialized files focused on their tool, protocol, interface class, platform, architecture, environment, ecosystem, or language.
- Do not let a tenet grant authority for edits, builds, deployment, destructive actions, credentials, or live-system mutation.
- Record public conclusions and evidence, not private chain-of-thought.
- Preserve the folder name `Programing Tenants` unless the user explicitly approves a rename.

## Validation

Before finishing a change:

- confirm every tenet ID is unique;
- confirm links resolve;
- scan for project-specific or sensitive content;
- check exact and semantic duplicates using decision, protected property, applicability, and boundary;
- merge redundant rules or replace them with canonical cross-references;
- confirm every indexed tenet file appears in `README.md` and every specialization references an applicable general owner;
- report files changed and any intentionally unresolved taxonomy question.
