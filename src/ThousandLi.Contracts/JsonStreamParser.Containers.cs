using System.Globalization;

namespace ThousandLi.Contracts;

public sealed partial class JsonStreamParser
{
    // 容器状态机分派器：按 ContainerState 列出所有字符转换分支，拆分会割裂状态转换的
    // 整体可读性，故保留为单方法。
    private void ProcessJsonChar(char c, List<JsonStreamEvent> events)
    {
        if (IsJsonWhitespace(c)) return;

        if (_frames.Count == 0)
        {
            if (_rootValueCompleted) ThrowUnexpected(c, "end of input");

            StartValue(c, events);
            return;
        }

        var frame = _frames[^1];
        switch (frame.State)
        {
            case ContainerState.ObjectExpectPropertyOrEnd:
                switch (c)
                {
                    case '}':
                        CompleteObject(events);
                        return;
                    case '"':
                        StartPropertyName();
                        return;
                    default:
                        ThrowUnexpected(c, "property name or '}'");
                        return;
                }

            case ContainerState.ObjectExpectProperty:
                if (c == '"')
                {
                    StartPropertyName();
                    return;
                }

                ThrowUnexpected(c, "property name");
                return;

            case ContainerState.ObjectExpectColon:
                if (c != ':') ThrowUnexpected(c, "':'");

                frame.State = ContainerState.ObjectExpectValue;
                return;

            case ContainerState.ObjectExpectValue:
                StartValue(c, events);
                return;

            case ContainerState.ObjectExpectCommaOrEnd:
                switch (c)
                {
                    case ',':
                        frame.State = ContainerState.ObjectExpectProperty;
                        return;
                    case '}':
                        CompleteObject(events);
                        return;
                    default:
                        ThrowUnexpected(c, "',' or '}'");
                        return;
                }

            case ContainerState.ArrayExpectValueOrEnd:
                if (c == ']')
                {
                    CompleteArray(events);
                    return;
                }

                StartValue(c, events);
                return;

            case ContainerState.ArrayExpectValue:
                StartValue(c, events);
                return;

            case ContainerState.ArrayExpectCommaOrEnd:
                switch (c)
                {
                    case ',':
                        frame.State = ContainerState.ArrayExpectValue;
                        return;
                    case ']':
                        CompleteArray(events);
                        return;
                    default:
                        ThrowUnexpected(c, "',' or ']'");
                        return;
                }

            default:
                throw new InvalidOperationException($"Unknown container state '{frame.State}'.");
        }
    }

    private void StartValue(char c, List<JsonStreamEvent> events)
    {
        if (!IsValueStart(c)) ThrowUnexpected(c, "JSON value");

        var path = BeginValue();
        switch (c)
        {
            case '{':
                events.Add(JsonStreamEvent.ObjectStarted(path));
                _frames.Add(new ContainerFrame(ContainerKind.Object, ContainerState.ObjectExpectPropertyOrEnd, path));
                return;
            case '[':
                events.Add(JsonStreamEvent.ArrayStarted(path));
                _frames.Add(new ContainerFrame(ContainerKind.Array, ContainerState.ArrayExpectValueOrEnd, path));
                return;
            case '"':
                BeginString(StringKind.Value, path, events);
                return;
            case 't':
                BeginLiteral("true", path);
                return;
            case 'f':
                BeginLiteral("false", path);
                return;
            case 'n':
                BeginLiteral("null", path);
                return;
            default:
                BeginNumber(c, path);
                return;
        }
    }

    private string BeginValue()
    {
        if (_frames.Count == 0)
        {
            if (_rootValueStarted) ThrowJson("Only one root JSON value is allowed");

            _rootValueStarted = true;
            return string.Empty;
        }

        var frame = _frames[^1];
        return frame.State switch
        {
            ContainerState.ObjectExpectValue => AppendPath(frame.Path,
                frame.PendingPropertyName ?? throw new InvalidOperationException("Object value has no property name.")),
            ContainerState.ArrayExpectValueOrEnd or ContainerState.ArrayExpectValue => AppendPath(frame.Path,
                frame.NextArrayIndex.ToString(CultureInfo.InvariantCulture)),
            _ => throw new JsonStreamException(
                $"Expected a JSON value, but the current container is in state '{frame.State}'", _position),
        };
    }

    private void CompleteValue()
    {
        if (_frames.Count == 0)
        {
            if (_rootValueCompleted) ThrowJson("Only one root JSON value is allowed");

            _rootValueCompleted = true;
            return;
        }

        var frame = _frames[^1];
        if (frame.Kind == ContainerKind.Object)
        {
            if (frame.State != ContainerState.ObjectExpectValue)
                ThrowJson($"Object value completed while object was in state '{frame.State}'");

            frame.PendingPropertyName = null;
            frame.State = ContainerState.ObjectExpectCommaOrEnd;
            return;
        }

        if (frame.State is not (ContainerState.ArrayExpectValueOrEnd or ContainerState.ArrayExpectValue))
            ThrowJson($"Array value completed while array was in state '{frame.State}'");

        frame.NextArrayIndex++;
        frame.State = ContainerState.ArrayExpectCommaOrEnd;
    }

    private void CompleteObject(List<JsonStreamEvent> events)
    {
        var frame = _frames[^1];
        if (frame.Kind != ContainerKind.Object) ThrowJson("Cannot close an object while parsing an array");

        _frames.RemoveAt(_frames.Count - 1);
        events.Add(JsonStreamEvent.ObjectCompleted(frame.Path));
        CompleteValue();
    }

    private void CompleteArray(List<JsonStreamEvent> events)
    {
        var frame = _frames[^1];
        if (frame.Kind != ContainerKind.Array) ThrowJson("Cannot close an array while parsing an object");

        _frames.RemoveAt(_frames.Count - 1);
        events.Add(JsonStreamEvent.ArrayCompleted(frame.Path));
        CompleteValue();
    }
}
