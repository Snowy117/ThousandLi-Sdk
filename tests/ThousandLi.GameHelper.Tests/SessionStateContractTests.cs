using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.GameHelper.Tests;

[SessionStateRoot]
public class TestSessionVariables
{
    [SessionStateMember] public virtual string? Name { get; set; }

    [SessionStateMember] public virtual long Score { get; set; }

    [SessionStateMember]
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global — Castle 代理写回需要 setter
    public virtual TrackedList<TestTurnRecord> TurnOutputs { get; set; } = [];
}

// ReSharper disable ClassWithVirtualMembersNeverInherited.Global, UnusedAutoPropertyAccessor.Global — 由 Castle 运行时代理继承并反射读取
public class TestTurnRecord
{
    [SessionStateMember] public virtual long TurnOrdinal { get; set; }

    [SessionStateMember] public virtual string? Label { get; set; }
}
// ReSharper restore ClassWithVirtualMembersNeverInherited.Global, UnusedAutoPropertyAccessor.Global

public sealed class SessionStateContractTests
{
    [Fact]
    public void MaterializeSessionStateEmitsGameHelperManagedRoot()
    {
        var state = SessionStateExtensions.MaterializeSessionState<TestSessionVariables>(Support.Player);

        var sessionVariables = state.GetProperty("_gameHelper").GetProperty("sessionVariables");
        Assert.Equal(JsonValueKind.Object, sessionVariables.ValueKind);
        Assert.Equal(0, sessionVariables.GetProperty("score").GetInt64());
    }

    [Fact]
    public void GetSessionStateOnActionContextWritesFineGrainedChanges()
    {
        var state = new GameState(Support.Json("{}"));
        var context = Support.BuildActionContext(state);

        var variables = context.GetSessionState<TestSessionVariables>();

        Assert.True(state.Contains(new JsonPointer("/_gameHelper/sessionVariables")));

        variables.Name = "雪之下";
        variables.Score = 42;

        var snapshot = state.Snapshot.GetProperty("_gameHelper").GetProperty("sessionVariables");
        Assert.Equal("雪之下", snapshot.GetProperty("name").GetString());
        Assert.Equal(42, snapshot.GetProperty("score").GetInt64());

        var replacePaths = state.Changes
            .Where(change => change.Operation == StateChangeOperation.Replace)
            .Select(change => change.Path.Value)
            .ToArray();
        Assert.Contains("/_gameHelper/sessionVariables/name", replacePaths);
        Assert.Contains("/_gameHelper/sessionVariables/score", replacePaths);
    }

    [Fact]
    public void TrackedListAddWritesAddChangeAtDashPath()
    {
        var state = new GameState(Support.Json("{}"));
        var context = Support.BuildActionContext(state);

        var variables = context.GetSessionState<TestSessionVariables>();
        variables.TurnOutputs.Add(new TestTurnRecord { TurnOrdinal = 1, Label = "turn" });

        var addChange = state.Changes.Single(change =>
            change is { Operation: StateChangeOperation.Add, Path.Value: "/_gameHelper/sessionVariables/turnOutputs/-" });
        Assert.NotNull(addChange.NewValue);

        var snapshot = state.Snapshot.GetProperty("_gameHelper").GetProperty("sessionVariables");
        Assert.Equal(1, snapshot.GetProperty("turnOutputs").GetArrayLength());
        Assert.Equal(1, snapshot.GetProperty("turnOutputs")[0].GetProperty("turnOrdinal").GetInt64());
    }

    [Fact]
    public void GetSessionStateOnFrontendRequestReadsCommittedSnapshot()
    {
        var state = new GameState(Support.Json(
            "{\"_gameHelper\":{\"sessionVariables\":{\"name\":\"雪之下\",\"score\":42,\"turnOutputs\":[]}}}"));
        var context = Support.BuildFrontendRequestContext(new ReadOnlyGameState(state.Snapshot));

        var variables = context.GetSessionState<TestSessionVariables>();

        Assert.Equal("雪之下", variables.Name);
        Assert.Equal(42, variables.Score);
    }

    [Fact]
    public void GetSessionStateOnFrontendRequestReturnsDefaultWhenUnmounted()
    {
        var context = Support.BuildFrontendRequestContext(new ReadOnlyGameState(Support.Json("{}")));

        var variables = context.GetSessionState<TestSessionVariables>();

        Assert.Equal(0, variables.Score);
    }

    [Fact]
    public void SessionStateContractRejectsSealedRoot()
    {
        var exception = Assert.Throws<SessionStateContractException>(
            () => SessionStateContract.Create(typeof(SealedRoot)));

        Assert.Contains("sealed", exception.Message);
    }

    [Fact]
    public void SessionStateContractRejectsMissingRootAttribute()
    {
        Assert.Throws<SessionStateContractException>(
            () => SessionStateContract.Create(typeof(UnattributedRoot)));
    }

    [Fact]
    public void SessionStateContractRejectsNonVirtualMember()
    {
        Assert.Throws<SessionStateContractException>(
            () => SessionStateContract.Create(typeof(NonVirtualMemberRoot)));
    }

    [SessionStateRoot]
    private sealed class SealedRoot;

    private class UnattributedRoot;

    [SessionStateRoot]
    public class NonVirtualMemberRoot
    {
        [SessionStateMember]
        // ReSharper disable once UnusedMember.Global — 仅用于触发非 virtual 成员的契约拒绝
        public string? Value { get; set; }
    }
}
