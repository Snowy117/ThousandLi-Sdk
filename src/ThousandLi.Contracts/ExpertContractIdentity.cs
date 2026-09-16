namespace ThousandLi.Contracts;

/// <summary>
/// Marks the abstract Expert category base type (the single public contract identity).
/// This is a pure identity attribute: the contract id is the type's own full name.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ExpertContractAttribute(string id) : Attribute
{
    public string Id { get; } = !string.IsNullOrWhiteSpace(id)
        ? id
        : throw new ArgumentException("Contract id cannot be null or whitespace.", nameof(id));
}
