using ApexCharts;
using FluentAssertions;
using KasaMonitor.Web.Ui;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class ApexJsTests
{
    [Fact]
    public void AFunctionOnAnObjectOption_IsSerializedForTheJsEval()
    {
        // The serializer Blazor-ApexCharts uses for the options (internal): the JS side evaluates only {"@eval": ...}.
        var serializer = typeof(YAxis).Assembly.GetType("ApexCharts.Internal.ChartSerializer")!;
        var options = (System.Text.Json.JsonSerializerOptions)serializer.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(m => m.Name == "GetOptions" && !m.IsGenericMethod).Invoke(null, null)!;

        var json = System.Text.Json.JsonSerializer.Serialize(new YAxis { Min = ApexJs.Function("function(min) { return min - 1; }") }, options);

        json.Should().Be("""{"min":{"@eval":"function(min) { return min - 1; }"}}""");
    }
}
