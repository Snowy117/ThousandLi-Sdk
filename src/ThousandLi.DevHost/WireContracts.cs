using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.DevHost;

internal static class WireContracts
{
    public static object ToWire(ActionRuntimeEvent runtimeEvent)
    {
        return runtimeEvent.Kind switch
        {
            ActionRuntimeEventKind.Started => new
            {
                type = "started",
                sessionId = runtimeEvent.SessionId.Value,
                branchId = runtimeEvent.BranchId.Value,
                actionId = runtimeEvent.ActionId.Value,
                actionRunId = runtimeEvent.ActionRunId.Value
            },
            ActionRuntimeEventKind.FrontendEvent => new
            {
                type = "frontendEvent",
                frontendEvent = new
                {
                    eventType = runtimeEvent.FrontendEvent!.EventType,
                    payload = runtimeEvent.FrontendEvent.Payload
                }
            },
            ActionRuntimeEventKind.Committed => new
            {
                type = "committed",
                terminalStatus = "Committed"
            },
            ActionRuntimeEventKind.Aborted => new
            {
                type = "aborted",
                terminalStatus = runtimeEvent.TerminalStatus.ToString(),
                errorMessage = runtimeEvent.ErrorMessage
            },
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeEvent))
        };
    }

    public static string SerializeLine(object value) => JsonSerializer.Serialize(value) + "\n";
}
