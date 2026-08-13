using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.GameAuthoring;

[JetBrains.Annotations.PublicAPI]
public static class AuthoringExtensions
{
    extension(GameState state)
    {
        public void Add<TValue>(string path, TValue value)
        {
            ArgumentNullException.ThrowIfNull(state);
            state.Add(new JsonPointer(path), JsonSerializer.SerializeToElement(value));
        }

        public void Replace<TValue>(string path, TValue value)
        {
            ArgumentNullException.ThrowIfNull(state);
            state.Replace(new JsonPointer(path), JsonSerializer.SerializeToElement(value));
        }
    }

    extension(IFrontendEventSink sink)
    {
        public ValueTask WriteAsync<TValue>(string eventType, TValue payload, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sink);
            return sink.WriteAsync(new FrontendEvent(eventType, JsonSerializer.SerializeToElement(payload)), cancellationToken);
        }
    }
}

public sealed class DelegateExpertSemanticEventSink(
    Func<ExpertSemanticEvent, CancellationToken, ValueTask> handler) : IExpertSemanticEventSink
{
    private readonly Func<ExpertSemanticEvent, CancellationToken, ValueTask> _handler =
        handler ?? throw new ArgumentNullException(nameof(handler));

    public ValueTask WriteAsync(ExpertSemanticEvent semanticEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticEvent);
        return _handler(semanticEvent, cancellationToken);
    }
}
