# Cerebral Guide: Model and Capability Selection

Object: Knowledge.Cerebral.Guide.ModelCapabilitySelection
Version: 1.2.0
Owner: Pegasus Cerebral
Triggers: selecting or changing a model, profile, reasoning level, media path, tool set, or context budget
Required capabilities: current model/profile catalog and capability metadata
Rendered budget: 4096 UTF-8 bytes

## Goal

Choose the least costly compatible profile that can complete the task with the required evidence and safety.

## Required-input vector

Classify the task before selection:

- input modes: text, image, audio, document, structured data, code;
- output modes and format requirements;
- reasoning depth and uncertainty;
- required tools or providers;
- expected context and artifact size;
- latency or cost sensitivity;
- privacy, locality, and authority constraints;
- required verification surface.

## Selection process

1. Reject profiles missing a required native input, output, tool, context, or locality capability.
2. Prefer the smallest compatible profile for routine bounded work.
3. Escalate reasoning or model capability for ambiguous architecture, difficult debugging, safety-critical judgment, or complex synthesis.
4. Re-evaluate only when task requirements materially change or the selected profile demonstrably cannot complete the work.
5. Preserve the active task state when switching; do not duplicate uncertain operations.

## Response balance

`go@XX` requests approximately XX percent of the currently usable context budget for the next response, within the model and host caps. The agent may request a different balance for a later run when measured input and planned output will not fit, then regenerate only the bounded unfinished response. Treat the value as a scheduling budget rather than a promise of exact token use. Report the effective input, reserve, and output budgets through metrics instead of guessing from combined bytes.

## Weak-model support

Use the same product truth, but provide more explicit state transitions, shorter operation sequences, bounded examples, and checklists. Do not give weaker models broader permissions or hide failure conditions.

## Media boundary

Capability metadata must distinguish native image/audio understanding, OCR, projection, transcription, and generated descriptions. Never claim a model inspected media when only a derived textual representation was provided.

## Failure and recovery

If the catalog is stale, a model rejects the input, or a required tool is absent, stop that route and select a verified compatible profile. Do not repeatedly retry an incompatible model or silently omit required evidence.

## Context efficiency

Load guide summaries and live schemas on demand. Use compact state and artifact references. More context is not automatically better context.
