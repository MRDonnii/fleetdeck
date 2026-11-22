using Fleetdeck.Server.Services;
using Xunit;

namespace Fleetdeck.Tests;

public class MqttTests
{
    /// <summary>Kører kun mod en rigtig broker: MQTT_TEST_HOST=host[:port].</summary>
    [Fact]
    public async Task PublishSubscribeRoundTrip()
    {
        var target = Environment.GetEnvironmentVariable("MQTT_TEST_HOST");
        if (string.IsNullOrEmpty(target)) return;
        var host = target.Split(':')[0];
        var port = target.Contains(':') ? int.Parse(target.Split(':')[1]) : 1883;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mq = await MqttConnection.Connect(host, port, "fleetdeck-test", null, null, "fdtest/status", "offline", cts.Token);
        await mq.Subscribe("fdtest/+/cmd", cts.Token);
        await Task.Delay(300, cts.Token);
        await mq.Publish("fdtest/7/cmd", "upgrade_all", false, cts.Token);
        var (topic, payload) = await mq.Receive(cts.Token);
        Assert.Equal("fdtest/7/cmd", topic);
        Assert.Equal("upgrade_all", payload);
        await mq.Ping(cts.Token);
        // stor besked (variabel længde over 127 og 16383 byte)
        var big = new string('æ', 9000);
        await mq.Publish("fdtest/8/cmd", big, false, cts.Token);
        var (_, p2) = await mq.Receive(cts.Token);
        Assert.Equal(big, p2);
    }
}
