# The Cortex handbook

An AI conversation is useful until the project outgrows the conversation.
You change subjects, start another chat, come back next week, or reach the
model's working-context limit. The useful decisions still matter. Repeating
the entire story every time is a poor way to recover them.

Pegasus Cortex gives that work a local home: project knowledge, task checkpoints,
selected retrieval and tools for doing practical work. Pegasus Cerebral is the
companion workspace that brings conversations, a configured model and workflows
together. You can use Cortex with other compatible clients too.

This handbook explains the ideas before the implementation details. Start with
the case study if you want to see the pieces working together. Open the reference
guides when you are ready to install, connect or automate something.

## Choose a starting point

| What you want to understand | Start here |
| --- | --- |
| Why an assistant needs more than a long chat | [Context and retrieval](context-and-retrieval.md) |
| How a local model, Cerebral and Cortex help each other | [Together case study](together.md) |
| What is inside the tool collection, including AutoItX | [Tool and feature map](tools-and-features.md) |
| How project files grew into this product | [The evolution of Cortex](evolution.md) |
| How to get started and keep the deployment local | [Setup and data boundaries](setup-and-data-boundaries.md) |
| The visual, interactive version | [Cortex product guide](https://cerebral.shaynewinter.com/cortex/) |

## A small vocabulary

**Model:** the software that generates an answer. A local model runs on a machine
you control; a hosted provider processes requests on its own infrastructure.
Cortex is not a model and does not train one.

**Working context:** the instructions, conversation and selected information
given to the model for the current answer. It has a size limit. Retained project
records inform a response only when the selected workflow supplies them; having
more records on disk does not mean they are in the model's working context.

**Continuity:** recovering enough reliable project state to carry on. A useful
checkpoint says what you were trying to do, what was established, what is blocked
and what should happen next.

**Tool:** an operation a client can request, such as reading a file or checking a
process. Having a tool available does not give an assistant permission to use it.

**MCP:** Model Context Protocol, a common interface through which compatible AI
clients discover and call tools. Cortex's shared HTTPS MCP endpoint is one way
to reach the running Cortex product; it is not another AI model.

**PAO:** Pegasus AgentOps, Cortex's typed local operations surface. A request
selects one operation and supplies its arguments separately. The relevant help
describes the fields that the installed version accepts.

## Three responsibilities

```text
You choose the objective and approve the work
                   |
        Cerebral or another client
        Conversation, workflow and model
                   |
                 Cortex
        Retained context and local tools
                   |
       Your selected files and local state
```

The model still has to reason. Cortex helps it recover relevant material and
work through explicit operations. A retained note is reference material, not
automatic truth or a new permission. A tool result needs to be interpreted at
the level it actually proves: a queued job, a completed operation and a verified
end-to-end workflow are different things.

## Availability and further reading

The [downloads page](https://cerebral.shaynewinter.com/downloads/) and
[GitHub releases](https://github.com/swpegasus/Pegasus-Cortex/releases) identify
prepared Cortex builds. The development source can contain changes newer than a
prepared download. Cerebral has its own development and availability status;
do not infer a Cerebral release or distribution grant from Cortex's release.

This handbook describes the maintained Windows-hosted architecture. Detailed
contracts and installation instructions remain in the [documentation index](../README.md).
Historical plans and experimental projects describe their own scope and are not
lists of automatically enabled features.

Cortex is created by **Shayne Winter**, with development assistance from
**OpenAI Codex** and **Pegasus Cerebral**. See the [project website](https://cerebral.shaynewinter.com/cortex/),
[source repository](https://github.com/swpegasus/Pegasus-Cortex),
[portfolio](https://shaynewinter.com/) and [operative license](../../LICENSE).
