#if FLEETDECK_LEGACY
using Avalonia.Controls;
namespace Fleetdeck.MacApp;
/// <summary>Avalonia 11 (legacy-bygget) kalder PlaceholderText for Watermark.</summary>
public static class LegacyAvalonia
{
    extension(TextBox t)
    {
        public string? PlaceholderText { get => t.Watermark; set => t.Watermark = value; }
    }
}
#endif
