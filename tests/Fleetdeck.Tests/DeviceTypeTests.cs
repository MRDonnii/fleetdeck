using Fleetdeck.Server.Data;

namespace Fleetdeck.Tests;

public class DeviceTypeTests
{
    [Theory]
    [InlineData("Apple TV HD", DeviceTypes.AppleTv)]
    [InlineData("AppleTV5,3", DeviceTypes.AppleTv)]
    [InlineData("apple tv 4K", DeviceTypes.AppleTv)]
    [InlineData("iPhone 15", DeviceTypes.Iphone)]
    [InlineData("iPad 10. gen", DeviceTypes.Ipad)]
    [InlineData(null, DeviceTypes.Ipad)]
    [InlineData("", DeviceTypes.Ipad)]
    public void Type_is_guessed_from_model(string? model, string expected) => Assert.Equal(expected, DeviceTypes.FromModel(model));

    [Theory]
    [InlineData("ipad", "ipad")]
    [InlineData(" AppleTV ", "appletv")]
    [InlineData("android", "android")]
    [InlineData("toaster", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Type_query_is_parsed(string? raw, string? expected) => Assert.Equal(expected, DeviceTypes.Parse(raw));

    [Fact]
    public void Apple_tv_requires_a_lan_ip()
    {
        Assert.NotNull(DeviceTypes.Validate(DeviceTypes.AppleTv, null));
        Assert.NotNull(DeviceTypes.Validate(DeviceTypes.AppleTv, "  "));
        Assert.NotNull(DeviceTypes.Validate(DeviceTypes.AppleTv, "stue"));
        Assert.Null(DeviceTypes.Validate(DeviceTypes.AppleTv, "192.168.1.50"));
        Assert.NotNull(DeviceTypes.Validate(DeviceTypes.Android, null));
    }

    [Fact]
    public void Ipad_lan_ip_is_optional_but_must_be_an_address()
    {
        Assert.Null(DeviceTypes.Validate(DeviceTypes.Ipad, null));
        Assert.Null(DeviceTypes.Validate(DeviceTypes.Iphone, "192.168.1.87"));
        Assert.NotNull(DeviceTypes.Validate(DeviceTypes.Ipad, "ikke-en-ip"));
        Assert.NotNull(DeviceTypes.Validate("toaster", "192.168.1.1"));
    }

    [Fact]
    public void New_devices_default_to_ipad() => Assert.Equal(DeviceTypes.Ipad, new Ipad().DeviceType);

    [Fact]
    public void Only_ios_types_get_profiles()
    {
        Assert.True(DeviceTypes.IsIos(DeviceTypes.Ipad));
        Assert.True(DeviceTypes.IsIos(DeviceTypes.Iphone));
        Assert.False(DeviceTypes.IsIos(DeviceTypes.AppleTv));
        Assert.False(DeviceTypes.IsIos(DeviceTypes.Android));
    }
}
