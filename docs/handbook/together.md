# Cerebral and Cortex together

Imagine returning to a project after a few days away. You remember the goal,
but not why a particular approach failed, which file was last checked, or what
the next chat was supposed to do. The model has no reliable way to guess those
facts. Giving it the entire conversation archive makes the working context
larger without guaranteeing that the useful decision will stand out.

The local-first approach is to keep the model's immediate work separate from
the project's durable record. Cerebral supplies the workspace and selected
model runtime. Cortex supplies organized project continuity and local tools.
The connection lets the current workflow retrieve useful material as needed.

This is an illustrative case study of that architecture, not a customer
deployment, benchmark or certification.

## The case study

A small team maintains a project on a controlled Windows workstation. It uses
several conversations for different parts of the work and a model configured
to run locally. Its aim is to keep project material and inference on controlled
equipment while avoiding the repeated setup of a fresh chat.

### 1 Recover the objective

The operator opens the project in Cerebral. The selected continuity workflow
asks Cortex for a bounded task brief using the actual conversation identity
and explicit project namespaces. A namespace is an organizational space for
the project, not a separate security tenant.

A useful brief might say:

```text
Objective: finish the export preview.
Verified: the preview opens; long filenames are clipped.
Blocker: the layout fix has not been checked at a narrow width.
Next: inspect the preview layout, then verify that width.
```

Those lines are a synthetic teaching example. Their value comes from a real
workflow recording verified state, rather than a model inventing a plausible
story about the previous session.

### 2 Recover the reason

The assistant searches the project's selected knowledge space for the earlier
layout decision. It first receives small previews. If a preview is relevant,
it requests the exact record with its type, identifier and revision.

This two-step pattern answers two different questions: "What might help?" and
"What does that selected record actually say?" Keeping them separate avoids
mistaking a short search summary for the complete source.

### 3 Give the local model relevant material

Cerebral can place the selected context into the model interaction through its
configured workflow. The local inference route runs the chosen native model
on controlled equipment. Context retrieval supplies facts and instructions;
it does not change the model's training or turn Cortex into a second model.

The practical aim is less repeated background in the prompt and more room for
the current problem. This is especially useful when a local model has a modest
working-context budget. It does not prove that a small model can solve every
problem a larger model can solve.

### 4 Check the actual project

An assistant can inspect an approved file through a typed PAO operation, then
use the returned contents to reason about the layout. A request should name the
file and keep the output bounded. It should not assume that an old checkpoint
proves what is currently on disk.

If work is authorized, a later tool call can make the specific change and a
focused check can establish the result. The sequence is still governed by the
operator's intent and the project's rules. Retrieval does not authorize a
write, an installation or a privileged action.

### 5 Leave the next conversation a useful starting point

After verification, the workflow records a compact checkpoint and any reusable
decision worth retaining. Task updates use revisions to detect conflicting
changes. Knowledge records can distinguish explicit preferences, verified
observations and inferences.

The project can continue in another conversation without treating an entire
transcript as the starting prompt. Durable project state survives the immediate
conversation. A live terminal, by contrast, lasts only while its owner Host
continues running; a reboot ends that terminal session.

## What each part contributes

| Part | Its responsibility | Why the other part benefits |
| --- | --- | --- |
| Cerebral | Conversation workspace, selected model execution and workflows | Makes retrieved context and tools useful within a continuing interaction |
| Cortex | Task and knowledge records, bounded retrieval and typed operations | Gives the workflow a project record beyond the current model context |
| Local model | Language generation and reasoning over supplied material | Can work from selected project facts without receiving the entire archive |
| Operator | Objectives, permissions, configuration and review | Keeps convenient automation tied to intended work |

```text
Selected local project
        |
        v
Cerebral asks for a brief ------> Cortex task / knowledge records
        ^                                   |
        |<------ bounded selected context --|
        |
Local model reasons over the supplied material
        |
        +---- authorized typed request ----> PAO / selected local resource
        |<----------- actual result --------|
        |
        +---- compact verified checkpoint -> Cortex
```

These are logical responsibilities, not a claim that every message invokes
every component. Enabled workflows, model placement and available tools determine
the route. Cortex also works with compatible clients other than Cerebral.

## Why smaller local models are a useful target

A limited context window is rather like a small workbench: useful tools and
the part being repaired should be within reach; every box in the workshop
does not need to sit on it. Cortex provides a way to look up the relevant box.

The resulting benefits are practical design goals:

- Recover project decisions instead of repeatedly explaining them from memory.
- Select a few relevant records instead of appending the entire archive.
- Keep ongoing tasks outside the temporary prompt.
- Receive useful tool results without routine receipt boilerplate.
- Read new terminal output incrementally during multi-stage work.

Context size, model capability, quantization, available RAM/VRAM and workload
still determine what fits and how well it runs. Retrieval has a cost, can miss
useful material and can return stale or incorrect records. There is no universal
token-saving percentage, speedup, hardware guarantee or model-quality equivalence
attached to this architecture.

## Keeping the deployment local

For a local-only configuration, provision the model files and dependencies
before isolation, select local model placement, keep Cortex on loopback when
other machines do not need it, and leave hosted providers, remote Workers and
unneeded network/plugin routes disabled. Protect the selected data directories
and account that owns the runtime.

The products have optional routes beyond one machine. A hosted provider sends
generation requests to that provider; a remote Worker changes where inference
runs; a network tool can contact an external service. Explicit LAN/VPN access
also extends the trust boundary beyond the workstation. A local database alone
does not make those configurations offline.

Application settings are not a substitute for an operating-system egress policy,
network isolation or plugin review. Numeric local operational measurements also
exist; "local-first" should not be read as a blanket claim of no telemetry in
every optional component.

## Compliance-sensitive environments

Controlled data placement, local inference, explicit tool use, durable project
state and understandable execution boundaries can support an organization's
deployment requirements. They make useful questions easier to answer: which
machine processed this request, where are the retained records, and what
operations were requested?

The organization still needs its own classification, access controls, retention
and deletion rules, backups, encryption policy, model and dependency licensing,
plugin review, network controls and validation. Sensitive content can appear in
files, records, screenshots, tool output and conversation history. Do not rely on
automatic secret detection to make arbitrary content safe to retain.

Cortex and Cerebral do not by themselves establish regulatory compliance, a
certification, tenant isolation, data-loss prevention or a guaranteed air gap.
The appropriate claim is a local deployment option with controls an operator
can configure and evaluate.

## Other suite products and availability

Pegasus Exchange is a separate cross-computer agent-delivery product. It is not
required for the same-machine case study above, and a diagram of Cerebral and
Cortex exchanging context does not assert a built-in Exchange connector.

Use the [interactive Together guide](https://cerebral.shaynewinter.com/together/)
for the visual explanation and [setup and data boundaries](setup-and-data-boundaries.md)
for the deployment checklist. Cortex's prepared release and Cerebral's own
development status remain separate.
