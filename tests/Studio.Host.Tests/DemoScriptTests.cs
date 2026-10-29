using System.Text.Json.Nodes;
using Studio.Host;
using Xunit;

namespace Studio.Host.Tests;

public class DemoScriptTests
{
    private static DemoPlan Parse(string json) => DemoScript.Parse(JsonNode.Parse(json));

    [Fact]
    public void A_typical_ai_demo_is_accepted_and_recorded_by_default()
    {
        var plan = Parse("""{"pace":"natural","steps":[{"do":"click","target":"Clipboard"},{"do":"type","text":"invoice"},{"do":"keys","keys":"Ctrl+F"},{"do":"scroll","amount":-2},{"do":"wait","seconds":1.5},{"do":"click","at":[0.5,0.25]}]}""");
        Assert.True(plan.Record);
        Assert.False(plan.Quick);
        Assert.Equal([DemoAction.Click, DemoAction.Type, DemoAction.Keys, DemoAction.Scroll, DemoAction.Wait, DemoAction.Click], plan.Steps.Select(s => s.Action));
        Assert.Equal([(ushort)0x11, (ushort)'F'], plan.Steps[2].Keys);
        Assert.Equal(-2, plan.Steps[3].Amount);
        Assert.Equal(.25, plan.Steps[5].Y);
    }

    [Theory]
    [InlineData("""{"steps":[]}""", "1–200 steps")]
    [InlineData("""{"steps":[{"do":"run","target":"cmd.exe"}]}""", "unknown action")]
    [InlineData("""{"steps":[{"do":"click"}]}""", "target")]
    [InlineData("""{"steps":[{"do":"click","target":"OK","script":"x"}]}""", "unsupported field")]
    [InlineData("""{"steps":[{"do":"click","at":[1.5,0]}]}""", "x must be")]
    [InlineData("""{"steps":[{"do":"wait","seconds":120}]}""", "seconds must be")]
    [InlineData("""{"steps":[{"do":"keys","keys":"Win+R"}]}""", "Windows key")]
    [InlineData("""{"steps":[{"do":"keys","keys":"Alt+F4"}]}""", "close or switch")]
    [InlineData("""{"steps":[{"do":"keys","keys":"Alt+Tab"}]}""", "close or switch")]
    [InlineData("""{"steps":[{"do":"keys","keys":"Ctrl+Shift+Esc"}]}""", "close or switch")]
    [InlineData("""{"steps":[{"do":"keys","keys":"Ctrl+Alt+Delete"}]}""", "close or switch")]
    [InlineData("""{"steps":[{"do":"click","target":"OK"}],"onFinish":"x"}""", "Unsupported demo field")]
    public void Unsafe_or_malformed_demos_are_refused_with_a_readable_reason(string json, string reason) =>
        Assert.Contains(reason, Assert.Throws<InvalidDataException>(() => Parse(json)).Message);

    [Fact]
    public void Very_long_demos_are_refused() =>
        Assert.Throws<InvalidDataException>(() => Parse("{\"steps\":[" + string.Join(',', Enumerable.Repeat("""{"do":"wait","seconds":30}""", 25)) + "]}"));

    [Fact]
    public void Targets_prefer_exact_names_and_clickable_controls()
    {
        (string, string)[] controls = [("Clipboard history", "Text"), ("Clipboard", "Text"), ("Clipboard", "ListItem"), ("Search clipboard", "Edit")];
        Assert.Equal(2, DemoScript.Match("clipboard", controls));
        Assert.Equal(0, DemoScript.Match("Clipboard hist", controls));
        Assert.Equal(3, DemoScript.Match("search", controls));
        Assert.Equal(-1, DemoScript.Match("Settings", controls));
        Assert.Equal(-1, DemoScript.Match("  ", controls));
    }

    [Fact]
    public void The_cursor_eases_to_exactly_the_target()
    {
        var path = DemoScript.CursorPath(0, 0, 100, 50, 10).ToList();
        Assert.Equal(10, path.Count);
        Assert.Equal((100, 50), path[^1]);
        Assert.True(path[1].X - path[0].X < path[5].X - path[4].X, "movement starts slowly and speeds up");
    }
}
