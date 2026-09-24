using Factory.Core;

namespace Factory.Core.Tests;

public sealed class TasksMdParserTests
{
    private const string Sample = """
        # Tracker

        ## In progress

        ## Next up

        ### Priority 1 — Do stuff

        - [ ] **SF-100 — First item**
          - Dependencies: SF-090, SF-091.
          - Scope: do the first thing.

        - [ ] **SF-101 — Second item** — Depends on SF-100 in prose only, not parsed.

        ## Optional backlog

        - [ ] **SF-900 — Never eligible** — sits outside the four recognized sections.

        ## Blocked

        ## Completed

        - [x] **SF-050 — Done already** — Completed 2026-01-01. Evidence here.
        """;

    [Fact]
    public void Parses_items_only_from_the_four_recognized_sections_in_file_order()
    {
        var items = TasksMdParser.Parse(Sample);

        Assert.Equal(["SF-100", "SF-101", "SF-050"], items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void An_item_under_a_priority_subheading_still_belongs_to_Next_up()
    {
        var item = TasksMdParser.Parse(Sample).Single(i => i.Id == "SF-100");
        Assert.Equal(TrackerSection.NextUp, item.Section);
        Assert.False(item.Checked);
    }

    [Fact]
    public void An_item_outside_the_four_recognized_sections_is_not_returned()
    {
        Assert.DoesNotContain(TasksMdParser.Parse(Sample), i => i.Id == "SF-900");
    }

    [Fact]
    public void Dependencies_line_is_parsed_but_inline_prose_is_not()
    {
        var first = TasksMdParser.Parse(Sample).Single(i => i.Id == "SF-100");
        Assert.Equal(["SF-090", "SF-091"], first.DependencyIds.ToArray());

        var second = TasksMdParser.Parse(Sample).Single(i => i.Id == "SF-101");
        Assert.Empty(second.DependencyIds);
    }

    [Fact]
    public void Completed_item_is_checked()
    {
        var done = TasksMdParser.Parse(Sample).Single(i => i.Id == "SF-050");
        Assert.True(done.Checked);
        Assert.Equal(TrackerSection.Completed, done.Section);
    }

    [Fact]
    public void FindSectionRange_returns_null_for_a_missing_canonical_heading()
    {
        Assert.Null(TasksMdParser.FindSectionRange("# Tracker\n\nNo sections here.\n", TrackerSection.NextUp));
    }

    [Fact]
    public void FindSectionRange_locates_the_lines_belonging_to_one_section()
    {
        var range = TasksMdParser.FindSectionRange(Sample, TrackerSection.Completed);
        Assert.NotNull(range);
        var lines = TasksMdParser.Normalize(Sample);
        Assert.Equal("## Completed", lines[range!.Value.HeaderLine]);
        Assert.Equal(lines.Count, range.Value.InsertBeforeLine);
    }

    [Fact]
    public void Real_repository_TASKS_md_is_parseable_without_throwing()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TASKS.md"))) root = root.Parent;
        Assert.NotNull(root);
        var content = File.ReadAllText(Path.Combine(root!.FullName, "TASKS.md"));

        var items = TasksMdParser.Parse(content);

        Assert.Contains(items, i => i.Id == "SF-618" && !i.Checked && i.Section == TrackerSection.InProgress);
        Assert.Contains(items, i => i.Id == "SF-619" && i.Checked && i.Section == TrackerSection.Completed);
        Assert.Contains(items, i => i.Id == "SF-710" && i.Checked && i.Section == TrackerSection.Completed);
        Assert.Contains(items, i => i.Id == "SF-703" && i.Checked && i.Section == TrackerSection.Completed);
    }
}
