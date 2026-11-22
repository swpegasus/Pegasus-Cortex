using System.Globalization;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    // These are retained observations, never permission to reopen, adopt or
    // terminate a process. Keep the generation pair together across recovery.
    private static void PreserveHelperGeneration(Dictionary<string, object?> data, OperationReceipt receipt,
        OperationReceipt? previous, string pidKey, string startKey)
    {
        var source = receipt.Data;
        if (!TryGeneration(source, out var pid, out var start))
        {
            if (receipt.State != "recovery_required" || previous is null || previous.Jid != receipt.Jid ||
                previous.Rid != receipt.Rid || previous.Op != receipt.Op ||
                !TryGeneration(previous.Data, out pid, out start)) return;
        }
        data[pidKey] = pid; data[startKey] = start;

        bool TryGeneration(IReadOnlyDictionary<string, object?>? candidate, out long generationPid, out string? generationStart)
        {
            generationPid = candidate?.GetValueOrDefault(pidKey) switch
            {
                int value => value, uint value => value, long value => value,
                JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var value) => value,
                _ => 0
            };
            generationStart = Text(candidate?.GetValueOrDefault(startKey));
            return generationPid is > 0 and <= uint.MaxValue && generationStart is { Length: <= 64 } &&
                DateTime.TryParseExact(generationStart, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) &&
                parsed.Kind == DateTimeKind.Utc && parsed.ToString("O", CultureInfo.InvariantCulture) == generationStart;
        }
    }
}
