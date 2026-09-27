using System.Diagnostics;
using System.Text.Json;
using Factory.Api;

namespace Factory.Api.Tests;

public sealed class AskRoutingEvaluationTests
{
    [Fact]
    public void Report_versioned_ask_routing_baseline()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AskRoutingEvaluation.v1.json");
        var cases = JsonSerializer.Deserialize<EvaluationCase[]>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(24, cases.Length);
        Assert.Equal(cases.Length, cases.Select(c => c.Id).Distinct().Count());

        var elapsed = new List<double>(cases.Length);
        var correct = 0;
        var falseDeterministic = 0;
        foreach (var item in cases)
        {
            var timer = Stopwatch.StartNew();
            var actual = OperatorQuestionParser.Parse(item.Text).Intent;
            timer.Stop();
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
            if (actual == item.ExpectedIntent) correct++;
            if (item.ExpectedIntent is null && actual is not null) falseDeterministic++;
            Console.WriteLine($"{item.Id} {item.Group}: expected={item.ExpectedIntent ?? "assistant"}, actual={actual ?? "assistant"}");
        }

        elapsed.Sort();
        Console.WriteLine($"Baseline: {correct}/{cases.Length} correct; {falseDeterministic} false deterministic routes; " +
            $"median={elapsed[elapsed.Count / 2]:F4}ms; p95={elapsed[(int)Math.Ceiling(elapsed.Count * .95) - 1]:F4}ms (single local run)");
    }

    private sealed record EvaluationCase(string Id, string Source, string Group, string Text, string? ExpectedIntent);
}
