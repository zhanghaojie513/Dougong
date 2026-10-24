using System.Diagnostics.Metrics;
using Dougong.Agent;
using Dougong.Ai;
using Microsoft.Extensions.AI;

namespace Dougong.Agent.Comparison;

/// <summary>测试辅助：读工具声明，不依赖 internal API。</summary>
internal static class TestToolDeclarations
{
    public static IReadOnlyList<ToolDescriptor> Read(ChatOptions? options) =>
        options.ReadToolDeclarations();
}

/// <summary>
/// 只观察"是否产生了输入 token 计量"的 listener，用于验证用量装饰器是否生效。
/// </summary>
internal sealed class DirectionalMeterListener : IDisposable
{
    private readonly MeterListener _listener = new();
    private volatile bool _sawInputTokens;
    private volatile bool _sawCalls;
    private volatile bool _sawUnreported;

    public DirectionalMeterListener()
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DougongTelemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            _ = value;
            _ = tags;
            _ = state;
            if (instrument.Name == DougongTelemetry.InputTokensInstrument) _sawInputTokens = true;
            if (instrument.Name == DougongTelemetry.CallsInstrument) _sawCalls = true;
            if (instrument.Name == DougongTelemetry.UnreportedCallsInstrument) _sawUnreported = true;
        });
    }

    public bool SawInputTokens => _sawInputTokens;
    public bool SawCalls => _sawCalls;
    public bool SawUnreported => _sawUnreported;

    public void Start() => _listener.Start();
    public void Dispose() => _listener.Dispose();
}
