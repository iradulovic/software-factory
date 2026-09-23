using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Parses a repository's own <c>TASKS.md</c> tracker file (SF-707) against this repository's own
/// documented convention — the same one <c>AGENTS.md</c>'s "Agent workflow" section describes and this very file
/// (<c>TASKS.md</c>) follows: a <c>## In progress</c> / <c>## Next up</c> / <c>## Completed</c> / <c>## Blocked</c>
/// section structure (an intervening <c>### Priority N</c> sub-heading does not change which of the four an item
/// belongs to; every other <c>##</c> heading — e.g. <c>## Optional backlog</c>, <c>## Deferred scope</c> — is
/// <see cref="TrackerSection.Other"/>, never eligible for automatic task creation), with each item written as
/// <c>- [ ] **SF-123 — Title**</c> (open) or <c>- [x] **SF-123 — Title**</c> (done), optionally followed by
/// indented continuation lines (a nested <c>- Dependencies: SF-1, SF-2.</c> line among them, this repository's own
/// convention for a machine-readable dependency declaration). Free-form prose elsewhere in an item — e.g. a
/// "Depends on SF-605 and SF-611" clause folded into the item's own description, as several existing items in this
/// very file do — is deliberately not parsed as a dependency; only the dedicated nested line is, exactly as
/// <see cref="IssueDependencyParser"/> deliberately parses only its own dedicated line, never surrounding prose.</summary>
public static partial class TasksMdParser
{
    [GeneratedRegex(@"^##\s+(.+?)\s*$")]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^-\s\[(?<check>[ xX])\]\s\*\*(?<id>SF-\d+)\s—\s(?<title>.+?)\*\*(?<rest>.*)$")]
    private static partial Regex ItemBullet();

    [GeneratedRegex(@"^\s*-\s*Dependencies:\s*(?<list>.+?)\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DependenciesLine();

    [GeneratedRegex(@"SF-\d+")]
    private static partial Regex TrackerItemRef();

    /// <summary>Every item found under one of the four recognized sections, in file order. Items under any other
    /// heading (including a completely unrecognized document with no matching headings at all) are simply absent
    /// — never an error, since a repository with no <c>TASKS.md</c> convention has nothing for SF-707 to parse.</summary>
    public static IReadOnlyList<TrackerItem> Parse(string content)
    {
        var lines = Normalize(content);
        var items = new List<TrackerItem>();
        var section = TrackerSection.Other;
        for (var i = 0; i < lines.Count; i++)
        {
            var headerMatch = SectionHeader().Match(lines[i]);
            if (headerMatch.Success) { section = MapHeader(headerMatch.Groups[1].Value); continue; }

            var itemMatch = ItemBullet().Match(lines[i]);
            if (!itemMatch.Success || section == TrackerSection.Other) continue;

            var start = i;
            var end = start + 1;
            while (end < lines.Count && lines[end].Length > 0 && char.IsWhiteSpace(lines[end][0])) end++;

            var dependencyIds = new List<string>();
            var descriptionLines = new List<string> { itemMatch.Groups["rest"].Value.Trim().TrimStart('—').Trim() };
            for (var d = start + 1; d < end; d++)
            {
                var dependenciesMatch = DependenciesLine().Match(lines[d]);
                if (dependenciesMatch.Success)
                    dependencyIds.AddRange(TrackerItemRef().Matches(dependenciesMatch.Groups["list"].Value).Select(m => m.Value));
                descriptionLines.Add(lines[d]);
            }

            items.Add(new TrackerItem(
                itemMatch.Groups["id"].Value,
                itemMatch.Groups["title"].Value,
                string.Join('\n', descriptionLines.Where(l => l.Length > 0)),
                itemMatch.Groups["check"].Value is "x" or "X",
                section,
                dependencyIds.Distinct().ToList(),
                start, end));

            i = end - 1;
        }
        return items;
    }

    /// <summary>The line range <paramref name="section"/>'s own items live in — from just after its <c>## </c>
    /// heading line up to (but excluding) the next top-level <c>## </c> heading, or end of file — or
    /// <see langword="null"/> if that canonical heading is not present in <paramref name="content"/> at all (a
    /// repository whose <c>TASKS.md</c> does not follow the documented convention).</summary>
    public static (int HeaderLine, int InsertBeforeLine)? FindSectionRange(string content, TrackerSection section)
    {
        var lines = Normalize(content);
        for (var i = 0; i < lines.Count; i++)
        {
            var headerMatch = SectionHeader().Match(lines[i]);
            if (!headerMatch.Success || MapHeader(headerMatch.Groups[1].Value) != section) continue;
            var end = i + 1;
            while (end < lines.Count && !SectionHeader().IsMatch(lines[end])) end++;
            return (i, end);
        }
        return null;
    }

    public static IReadOnlyList<string> Normalize(string content) => content.Replace("\r\n", "\n").Split('\n');

    private static TrackerSection MapHeader(string text) => text.Trim() switch
    {
        "In progress" => TrackerSection.InProgress,
        "Next up" => TrackerSection.NextUp,
        "Completed" => TrackerSection.Completed,
        "Blocked" => TrackerSection.Blocked,
        _ => TrackerSection.Other
    };
}
