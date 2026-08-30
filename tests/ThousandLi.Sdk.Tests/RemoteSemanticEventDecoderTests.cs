using System.Text;
using ThousandLi.RemoteExperts;

namespace ThousandLi.Sdk.Tests;

public sealed class RemoteSemanticEventDecoderTests
{
    private static async ValueTask<List<RemoteExpertStreamFrame>> DecodeAsync(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var frames = new List<RemoteExpertStreamFrame>();
        await foreach (var frame in RemoteSemanticEventDecoder.DecodeAsync(stream, TestSupport.CancellationToken))
            frames.Add(frame);
        return frames;
    }

    [Fact]
    public async Task DecodeAsync_DecodesEventFrames_WithOrdinalsAndPayloads()
    {
        var frames = await DecodeAsync(
            """
            id: 0
            data: {"type":"event","eventType":"delta","payload":{"text":"你好"}}

            id: 1
            data: {"type":"event","eventType":"delta","payload":{"text":"世界"}}

            """);

        Assert.Equal(2, frames.Count);
        var first = Assert.IsType<RemoteExpertEventFrame>(frames[0]);
        Assert.Equal(0, first.Ordinal);
        Assert.Equal("delta", first.EventType);
        Assert.Equal("你好", first.Payload.GetProperty("text").GetString());
        var second = Assert.IsType<RemoteExpertEventFrame>(frames[1]);
        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    public async Task DecodeAsync_SkipsKeepaliveComments_AndToleratesUnknownFields()
    {
        var frames = await DecodeAsync(
            """
            : keepalive

            event: message
            id: 7
            retry: 1000
            data: {"type":"event","eventType":"tick","payload":{}}

            """);

        var frame = Assert.Single(frames);
        var eventFrame = Assert.IsType<RemoteExpertEventFrame>(frame);
        Assert.Equal(7, eventFrame.Ordinal);
        Assert.Equal("tick", eventFrame.EventType);
    }

    [Fact]
    public async Task DecodeAsync_DecodesCompletedFrame_WithAndWithoutOutput()
    {
        var withOutput = await DecodeAsync(
            "data: {\"type\":\"completed\",\"output\":{\"ok\":true}}\n\n");
        var completed = Assert.IsType<RemoteExpertCompletedFrame>(Assert.Single(withOutput));
        Assert.NotNull(completed.Output);
        Assert.True(completed.Output!.Value.GetProperty("ok").GetBoolean());

        var withoutOutput = await DecodeAsync("data: {\"type\":\"completed\"}\n\n");
        var bare = Assert.IsType<RemoteExpertCompletedFrame>(Assert.Single(withoutOutput));
        Assert.Null(bare.Output);
    }

    [Fact]
    public async Task DecodeAsync_DecodesFailedFrame_WithCodeAndMessage()
    {
        var frames = await DecodeAsync(
            "data: {\"type\":\"failed\",\"code\":\"timeout\",\"message\":\"took too long\"}\n\n");

        var failed = Assert.IsType<RemoteExpertFailedFrame>(Assert.Single(frames));
        Assert.Equal("timeout", failed.Code);
        Assert.Equal("took too long", failed.Message);
    }

    [Fact]
    public async Task DecodeAsync_DecodesCancelledFrame()
    {
        var frames = await DecodeAsync("data: {\"type\":\"cancelled\"}\n\n");
        _ = Assert.IsType<RemoteExpertCancelledFrame>(Assert.Single(frames));
    }

    [Fact]
    public async Task DecodeAsync_JoinsMultiLineData_WithNewlines()
    {
        var frames = await DecodeAsync(
            """
            id: 0
            data: {"type":"event","eventType":"delta",
            data:  "payload":{"text":"x"}}

            """);

        var eventFrame = Assert.IsType<RemoteExpertEventFrame>(Assert.Single(frames));
        Assert.Equal("x", eventFrame.Payload.GetProperty("text").GetString());
    }

    [Fact]
    public async Task DecodeAsync_DispatchesTrailingFrame_AtEndOfStream()
    {
        var frames = await DecodeAsync("data: {\"type\":\"cancelled\"}");
        _ = Assert.IsType<RemoteExpertCancelledFrame>(Assert.Single(frames));
    }

    [Fact]
    public async Task DecodeAsync_DecodesDataRequestFrames_AllThreeViews()
    {
        var frames = await DecodeAsync(
            """
            id: 0
            data: {"type":"dataRequest","requestId":"r-1","bucketId":"b0","view":"description"}

            id: 1
            data: {"type":"dataRequest","requestId":"r-2","bucketId":"b0","view":"rawTurns","cursor":4,"limit":200}

            id: 2
            data: {"type":"dataRequest","requestId":"r-3","bucketId":"b1","view":"compressedView"}

            """);

        Assert.Equal(3, frames.Count);
        var description = Assert.IsType<RemoteExpertDataRequestFrame>(frames[0]);
        Assert.Equal(0, description.Ordinal);
        Assert.Equal("r-1", description.RequestId);
        Assert.Equal("b0", description.BucketId);
        Assert.Equal(RemoteExpertDataView.Description, description.View);
        Assert.Null(description.Cursor);
        Assert.Null(description.Limit);

        var rawTurns = Assert.IsType<RemoteExpertDataRequestFrame>(frames[1]);
        Assert.Equal(RemoteExpertDataView.RawTurns, rawTurns.View);
        Assert.Equal(4, rawTurns.Cursor);
        Assert.Equal(200, rawTurns.Limit);

        var compressed = Assert.IsType<RemoteExpertDataRequestFrame>(frames[2]);
        Assert.Equal(RemoteExpertDataView.CompressedView, compressed.View);
        Assert.Equal("b1", compressed.BucketId);
    }

    [Fact]
    public async Task DecodeAsync_RejectsDataRequestFramesWithMissingFields()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"dataRequest","bucketId":"b0","view":"rawTurns"}

                """).AsTask());
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"dataRequest","requestId":"r-1","view":"rawTurns"}

                """).AsTask());
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"dataRequest","requestId":"r-1","bucketId":"b0"}

                """).AsTask());
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync("data: {\"type\":\"dataRequest\",\"requestId\":\"r-1\",\"bucketId\":\"b0\",\"view\":\"rawTurns\"}\n\n")
                .AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsDataRequestFramesWithBadViewOrPagingValues()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"dataRequest","requestId":"r-1","bucketId":"b0","view":"everything"}

                """).AsTask());
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"dataRequest","requestId":"r-1","bucketId":"b0","view":"rawTurns","cursor":-1}

                """).AsTask());
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"dataRequest","requestId":"r-1","bucketId":"b0","view":"rawTurns","limit":0}

                """).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_EnforcesMonotonicOrdinalsAcrossEventAndDataRequestFrames()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 3
                data: {"type":"dataRequest","requestId":"r-1","bucketId":"b0","view":"description"}

                id: 3
                data: {"type":"event","eventType":"chunk","payload":{}}

                """).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsNonMonotonicOrdinals()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 3
                data: {"type":"event","eventType":"a","payload":{}}

                id: 3
                data: {"type":"event","eventType":"b","payload":{}}

                """).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsEventFrameWithoutId()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync("data: {\"type\":\"event\",\"eventType\":\"a\",\"payload\":{}}\n\n").AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsNegativeOrNonNumericOrdinals()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: -1
                data: {"type":"event","eventType":"a","payload":{}}

                """).AsTask());
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: abc
                data: {"type":"event","eventType":"a","payload":{}}

                """).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsEventFrameWithoutEventTypeOrPayload()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"event","payload":{}}

                """).AsTask());
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync(
                """
                id: 0
                data: {"type":"event","eventType":"a"}

                """).AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsFailedFrameWithoutCode()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync("data: {\"type\":\"failed\",\"message\":\"boom\"}\n\n").AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsUnknownFrameType()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync("data: {\"type\":\"surprise\"}\n\n").AsTask());
    }

    [Fact]
    public async Task DecodeAsync_RejectsInvalidFrameJson()
    {
        _ = await Assert.ThrowsAsync<RemoteProtocolException>(() =>
            DecodeAsync("data: {not json}\n\n").AsTask());
    }
}
