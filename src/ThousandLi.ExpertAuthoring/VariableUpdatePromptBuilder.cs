using System.Text.Json;
using ThousandLi.Contracts;

namespace ThousandLi.ExpertAuthoring;

/// <summary>共享变量更新第二遍调用的提示词装配器。</summary>
internal static class VariableUpdatePromptBuilder
{
    internal static string Build(
        string worldSettings,
        string previousState,
        string newInformation,
        string stateSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(newInformation);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousState);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateSchema);

        using var lease = PromptBufferPool.Rent();
        var writer = lease.Writer;
        writer.Append($$"""
            你是变量更新器，负责根据最新剧情，使用 JSON Patch 类似的方式进行变量更新。

            <BasicInformation>
            {{worldSettings}}
            </BasicInformation>

            <PreviousState>
            {{previousState}}
            </PreviousState>

            <NewInformation>
            {{newInformation}}
            </NewInformation>

            <StateSchema>
            {{stateSchema}}
            </StateSchema>

            <Task>
            instructions:
              - 在思考中分析，但最终输出必须是一个 object，其中 variableUpdates 是实际更新命令数组
              - 必须立即输出全部更新命令
              - 更新命令类似 JSON Patch (RFC 6902)，仅支持 replace、delta、insert、remove
              - replace：替换已有路径的值
              - delta：以数值增量更新已有数值路径；整数目标仍只能使用整数增量
              - insert：向 object 或 array 插入项目；array 使用 - 表示追加
              - remove：删除 object 成员或 array 项
              - 不得更新以 _ 开头的只读字段，例如 _变量
              - 遵循 StateSchema 中的规则
            format: |-
              {
                "variableUpdates": [
                  { "op": "replace", "path": "/path/to/variable", "value": "new_value" },
                  { "op": "delta", "path": "/path/to/number/variable", "value": 1 },
                  { "op": "insert", "path": "/path/to/array/-", "value": "new_value" },
                  { "op": "remove", "path": "/path/to/object/key" }
                ]
              }
            任务:
              description: 禁止角色扮演或续写剧情，只能按照给定格式更新变量
              reference: NewInformation 是最新剧情，PreviousState 是剧情发生前的状态
              rule: 以旁白视角分析剧情后的变量变化，并按规则生成更新命令
              output: 必须输出带 variableUpdates 数组的 object
            </Task>

            作为变量更新器，请阅读新剧情并更新变量。
            """);

        return lease.Buffer.ToString();
    }

    internal static string FormatNewInformation(JsonElement mainOutput, string? reasoning = null) =>
        string.IsNullOrWhiteSpace(reasoning)
            ? mainOutput.GetRawText()
            : $"{reasoning}\n{mainOutput.GetRawText()}";
}
