using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

/// <summary>
/// Marks a process tree launched while the single-owner mutation worker is
/// occupied. A descendant may still perform read-only PAO operations, but it
/// must not enqueue another mutation and wait for the worker held by its own
/// ancestor.
/// </summary>
internal static class AgentOpsExecutionContext
{
    internal const string MutationDepthEnvironmentVariable = "PEGASUS_PAO_MUTATION_DEPTH";
    private const string ActiveMutationValue = "1";

    internal static ToolInvocation MarkMutationChild(ToolInvocation invocation) =>
        invocation.WithEnvironment(
            invocation.Environment.WithInternal(
                MutationDepthEnvironmentVariable,
                ActiveMutationValue));

    internal static void MarkMutationChildEnvironment(IDictionary<string,string> environment) =>
        environment[MutationDepthEnvironmentVariable] = ActiveMutationValue;

    internal static bool RejectsNestedMutation(OperationRequest request) =>
        Environment.GetEnvironmentVariable(MutationDepthEnvironmentVariable) == ActiveMutationValue &&
        AgentOpsEngine.IsMutation(request);
}
