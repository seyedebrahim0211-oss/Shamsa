using System.IO;
using System.Text.Json;
using Shamsa.Desktop;
using Shamsa.Desktop.Widgets;
using Xunit;

namespace Shamsa.Tests;

public class EyvanRegressionTests
{
    private static string Snapshot(object value)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(value));
        return path;
    }

    [Fact]
    public void FreshEyvanCoordinatesOverrideManualLocation()
    {
        string path = Snapshot(new { schema = 1, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            city = "بغداد", nextName = "ظهر", nextAt = 0, latitude = 33.3152, longitude = 44.3661, zoneId = "Asia/Baghdad" });
        try
        {
            var location = CityClock.Resolve(new WidgetsSettings { EyvanFile = path, City = "تهران" });
            Assert.True(location.FromEyvan);
            Assert.Equal("بغداد", location.City);
            Assert.Equal(33.3152, location.Latitude);
            Assert.Equal(TimeSpan.FromHours(3), location.Zone.GetUtcOffset(DateTime.UtcNow));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LegacyCityOnlySnapshotResolvesLocation()
    {
        string path = Snapshot(new { schema = 1, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            city = "بیروت", nextName = "", nextAt = 0 });
        try { Assert.True(CityClock.Resolve(new WidgetsSettings { EyvanFile = path }).FromEyvan); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StaleSnapshotPreservesManualLocation()
    {
        string path = Snapshot(new { schema = 1, at = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(),
            city = "بغداد", nextName = "", nextAt = 0 });
        try
        {
            var location = CityClock.Resolve(new WidgetsSettings { EyvanFile = path, City = "تهران", ZoneId = "Asia/Tehran" });
            Assert.False(location.FromEyvan);
            Assert.Equal("تهران", location.City);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(100, 44)]
    [InlineData(33, -200)]
    public void InvalidCoordinatesRejected(double latitude, double longitude)
    {
        string path = Snapshot(new { schema = 1, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            city = "بغداد", nextName = "", nextAt = 0, latitude, longitude, zoneId = "Asia/Baghdad" });
        try { Assert.Null(EyvanBridge.Read(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FiveWorldClocksResolve()
    {
        Assert.Equal(5, CityClock.Defaults.Count);
        foreach (var id in CityClock.Defaults.Keys) Assert.NotNull(CityClock.Zone(id));
    }
}
