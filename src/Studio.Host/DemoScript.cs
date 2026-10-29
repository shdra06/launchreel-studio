using System.Globalization;
using System.Text.Json.Nodes;

namespace Studio.Host;

public enum DemoAction { Click, DoubleClick, RightClick, Hover, Type, Keys, Scroll, Wait }

/// <summary>One action the real app receives. Positions are fractions of the app window.</summary>
public sealed record DemoStep(DemoAction Action, string? Target = null, double? X = null, double? Y = null, string? Text = null, IReadOnlyList<ushort>? Keys = null, int Amount = 0, double Seconds = 0);
public sealed record DemoPlan(IReadOnlyList<DemoStep> Steps, bool Record, bool Quick);

/// <summary>
/// Validates demo scripts written by an AI or by hand (mirrors validateDemo in web/project.ts). A script is plain data:
/// it can point, click, type, press keys, scroll and wait inside the connected app and nothing else.
/// </summary>
public static class DemoScript
{
    private static readonly Dictionary<string, DemoAction> Actions = new(StringComparer.Ordinal)
    {
        ["click"] = DemoAction.Click, ["doubleClick"] = DemoAction.DoubleClick, ["rightClick"] = DemoAction.RightClick, ["hover"] = DemoAction.Hover,
        ["type"] = DemoAction.Type, ["keys"] = DemoAction.Keys, ["scroll"] = DemoAction.Scroll, ["wait"] = DemoAction.Wait,
    };
    private static readonly HashSet<string> StepFields = ["do", "target", "at", "text", "keys", "amount", "seconds"];

    public static DemoPlan Parse(JsonNode? node)
    {
        if (node is not JsonObject demo) throw new InvalidDataException("The demo must be a JSON object.");
        foreach (var (key, _) in demo) if (key is not ("steps" or "record" or "pace")) throw new InvalidDataException($"Unsupported demo field: {key}");
        if (demo["steps"] is not JsonArray steps || steps.Count is 0 or > 200) throw new InvalidDataException("A demo needs 1–200 steps.");
        var result = new List<DemoStep>();
        var seconds = 0.0;
        for (var i = 0; i < steps.Count; i++)
        {
            var label = $"Demo step {i + 1}";
            if (steps[i] is not JsonObject s) throw new InvalidDataException($"{label} must be an object.");
            foreach (var (key, _) in s) if (!StepFields.Contains(key)) throw new InvalidDataException($"{label}: unsupported field {key}");
            var name = Text(s["do"], 20, $"{label} action");
            if (!Actions.TryGetValue(name, out var action)) throw new InvalidDataException($"{label}: unknown action “{name}”.");
            var step = new DemoStep(action);
            if (action is DemoAction.Click or DemoAction.DoubleClick or DemoAction.RightClick or DemoAction.Hover or DemoAction.Scroll)
            {
                if (s["target"] is not null) step = step with { Target = Text(s["target"], 200, $"{label} target") };
                if (s["at"] is JsonArray at)
                {
                    if (at.Count != 2) throw new InvalidDataException($"{label}: at must be [x, y] fractions of the window.");
                    step = step with { X = Number(at[0], 0, 1, $"{label} x"), Y = Number(at[1], 0, 1, $"{label} y") };
                }
                else if (s["at"] is not null) throw new InvalidDataException($"{label}: at must be [x, y] fractions of the window.");
                if (action != DemoAction.Scroll && step.Target is null && step.X is null) throw new InvalidDataException($"{label}: give a target (a control's name) or at: [x, y].");
            }
            switch (action)
            {
                case DemoAction.Type: step = step with { Text = Text(s["text"], 500, $"{label} text") }; seconds += step.Text!.Length * .06; break;
                case DemoAction.Keys: step = step with { Keys = ParseKeys(Text(s["keys"], 60, $"{label} keys")) }; seconds += .5; break;
                case DemoAction.Scroll: step = step with { Amount = (int)Math.Round(s["amount"] is null ? 3 : Number(s["amount"], -50, 50, $"{label} amount")) }; seconds += .8; break;
                case DemoAction.Wait: step = step with { Seconds = Number(s["seconds"], 0, 30, $"{label} seconds") }; seconds += step.Seconds; break;
                default: seconds += .8; break;
            }
            result.Add(step);
        }
        if (seconds > 600) throw new InvalidDataException("Keep demos under 10 minutes.");
        return new DemoPlan(result, demo["record"]?.GetValue<bool>() != false, demo["pace"]?.GetValue<string>() == "quick");
    }

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["insert"] = 0x2D,
    };

    /// <summary>
    /// "Ctrl+Shift+F" → virtual-key codes, modifiers first. The Windows key and system shortcuts that close or switch
    /// apps are refused, so a script can never leave the app it is demonstrating.
    /// </summary>
    public static IReadOnlyList<ushort> ParseKeys(string keys)
    {
        var parts = keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4) throw new InvalidDataException($"Keys look like \"Ctrl+F\" or \"Enter\", not “{keys}”.");
        var codes = new List<ushort>();
        foreach (var part in parts)
        {
            if (part.Equals("win", StringComparison.OrdinalIgnoreCase) || part.Equals("windows", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Demos cannot press the Windows key.");
            if (Named.TryGetValue(part, out var named)) codes.Add(named);
            else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) codes.Add(char.ToUpperInvariant(part[0]));
            else if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 12) codes.Add((ushort)(0x6F + f));
            else throw new InvalidDataException($"Unknown key “{part}”.");
        }
        var alt = codes.Contains(0x12);
        if (ClosesOrSwitches(codes)) throw new InvalidDataException("Demos cannot use shortcuts that close or switch apps.");
        return codes.OrderBy(c => c is 0x11 or 0x10 or 0x12 ? 0 : 1).ToList();
    }

    /// <summary>
    /// Shortcuts that close, quit or switch apps (Alt+F4, Alt+Tab, Alt+Esc, Alt+Space's window menu, Ctrl+Q, Ctrl+W,
    /// Ctrl+F4, Ctrl+Esc, Ctrl+Alt+Del). Also used for Live 3D typing.
    /// </summary>
    public static bool ClosesOrSwitches(IReadOnlyCollection<ushort> codes)
    {
        bool Has(ushort code) => codes.Contains(code);
        var alt = Has(0x12); var ctrl = Has(0x11);
        return (alt && (Has(0x73) || Has(0x09) || Has(0x1B) || Has(0x20))) || (ctrl && (Has(0x51) || Has(0x57) || Has(0x73) || Has(0x1B))) || (ctrl && alt && Has(0x2E));
    }

    private static readonly string[] Clickable = ["ListItem", "TabItem", "Button", "MenuItem", "Hyperlink", "TreeItem", "CheckBox", "RadioButton", "ComboBox", "Edit"];

    /// <summary>Best control for a target name: exact match, then prefix, then contains; clickable controls first.</summary>
    public static int Match(string target, IReadOnlyList<(string Name, string Type)> candidates)
    {
        static string Norm(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var wanted = Norm(target);
        if (wanted.Length == 0) return -1;
        int Rank(string name)
        {
            var n = Norm(name);
            return n == wanted ? 0 : n.StartsWith(wanted, StringComparison.Ordinal) ? 1 : n.Contains(wanted, StringComparison.Ordinal) ? 2 : -1;
        }
        var best = -1; var bestScore = int.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var rank = Rank(candidates[i].Name);
            if (rank < 0) continue;
            var type = Array.IndexOf(Clickable, candidates[i].Type);
            var score = rank * 100 + (type < 0 ? 50 : type);
            if (score < bestScore) { best = i; bestScore = score; }
        }
        return best;
    }

    public static string Describe(DemoStep step) => step.Action switch
    {
        DemoAction.Type => $"Typing “{(step.Text!.Length > 30 ? step.Text[..30] + "…" : step.Text)}”",
        DemoAction.Keys => "Pressing keys",
        DemoAction.Wait => $"Waiting {step.Seconds.ToString("0.#", CultureInfo.InvariantCulture)} s",
        DemoAction.Scroll => step.Amount >= 0 ? "Scrolling down" : "Scrolling up",
        _ => $"{step.Action switch { DemoAction.DoubleClick => "Double-clicking", DemoAction.RightClick => "Right-clicking", DemoAction.Hover => "Pointing at", _ => "Clicking" }} {(step.Target is null ? "a spot" : $"“{step.Target}”")}",
    };

    /// <summary>Eased cursor positions from one point to another, so recorded pointer movement looks like a person's.</summary>
    public static IEnumerable<(int X, int Y)> CursorPath(int fromX, int fromY, int toX, int toY, int steps)
    {
        steps = Math.Max(1, steps);
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var e = t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            yield return ((int)Math.Round(fromX + (toX - fromX) * e), (int)Math.Round(fromY + (toY - fromY) * e));
        }
    }

    private static string Text(JsonNode? node, int max, string label)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > max) throw new InvalidDataException($"{label} must be text up to {max} characters.");
        return text;
    }

    private static double Number(JsonNode? node, double min, double max, string label)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out var number) || !double.IsFinite(number) || number < min || number > max)
            throw new InvalidDataException($"{label} must be {min}–{max}.");
        return number;
    }
}
