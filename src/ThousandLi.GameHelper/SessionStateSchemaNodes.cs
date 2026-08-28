namespace ThousandLi.GameHelper;

// 多态 schema 节点层级：抽象基 record 供 SessionStateTypeNode 等继承（AI 投影 / schema 渲染 / patch 应用共享的类型视图）。
internal abstract record SessionStateSchemaNode;

internal sealed record SessionStatePrimitiveNode(string Kind) : SessionStateSchemaNode
{
    public static SessionStatePrimitiveNode String { get; } = new("string");
    public static SessionStatePrimitiveNode Boolean { get; } = new("boolean");
}

internal sealed record SessionStateNumberNode(bool Integer, Type ClrType) : SessionStateSchemaNode;

internal sealed record SessionStateEnumNode(Type EnumType) : SessionStateSchemaNode;

internal sealed record SessionStateListNode(SessionStateSchemaNode Item) : SessionStateSchemaNode;

internal sealed record SessionStateDictionaryNode(SessionStateSchemaNode Value) : SessionStateSchemaNode;
