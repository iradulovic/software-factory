using Factory.Core;

namespace Factory.Core.Tests;

public sealed class TasksMdWriterTests
{
    private const string Sample = """
        # Tracker

        ## In progress

        ## Next up

        - [ ] **SF-100 — First item**
          - Dependencies: SF-090.
          - Scope: do the first thing.

        - [ ] **SF-101 — Second item**

        ## Blocked

        ## Completed

        - [x] **SF-050 — Done already** — Completed 2026-01-01. Evidence here.
        """;

    [Fact]
    public void Moving_an_item_to_In_progress_relocates_its_full_block_and_keeps_the_checkbox_unchecked()
    {
        var updated = TasksMdWriter.Apply(Sample, "SF-100", TrackerSection.InProgress, null);
        Assert.NotNull(updated);

        var items = TasksMdParser.Parse(updated!);
        var moved = items.Single(i => i.Id == "SF-100");
        Assert.Equal(TrackerSection.InProgress, moved.Section);
        Assert.False(moved.Checked);
        Assert.Equal(["SF-090"], moved.DependencyIds.ToArray());
        // The other Next up item is untouched and still parses in its original place.
        Assert.Equal(TrackerSection.NextUp, items.Single(i => i.Id == "SF-101").Section);
        Assert.Equal(TrackerSection.Completed, items.Single(i => i.Id == "SF-050").Section);
    }

    [Fact]
    public void Completing_an_item_checks_it_and_appends_the_given_note()
    {
        var updated = TasksMdWriter.Apply(Sample, "SF-101", TrackerSection.Completed, "Completed 2026-09-23 by Software Factory.");
        Assert.NotNull(updated);

        var completed = TasksMdParser.Parse(updated!).Single(i => i.Id == "SF-101");
        Assert.True(completed.Checked);
        Assert.Equal(TrackerSection.Completed, completed.Section);
        Assert.Contains("Completed 2026-09-23 by Software Factory.", completed.Description);
    }

    [Fact]
    public void Blocking_an_item_appends_the_unblock_condition_as_a_nested_bullet()
    {
        var updated = TasksMdWriter.Apply(Sample, "SF-100", TrackerSection.Blocked, "Blocked automatically: build failed.");
        Assert.NotNull(updated);

        var blocked = TasksMdParser.Parse(updated!).Single(i => i.Id == "SF-100");
        Assert.Equal(TrackerSection.Blocked, blocked.Section);
        Assert.False(blocked.Checked);
        Assert.Contains("Blocked automatically: build failed.", blocked.Description);
        // The Dependencies/Scope bullets that came with the item survived the move untouched.
        Assert.Contains("Dependencies: SF-090.", blocked.Description);
    }

    [Fact]
    public void Applying_the_same_transition_twice_is_a_no_op_the_second_time()
    {
        var first = TasksMdWriter.Apply(Sample, "SF-100", TrackerSection.InProgress, null);
        Assert.NotNull(first);
        var second = TasksMdWriter.Apply(first!, "SF-100", TrackerSection.InProgress, null);
        Assert.Null(second);
    }

    [Fact]
    public void Completing_with_the_same_note_twice_is_idempotent_but_a_different_note_still_applies()
    {
        var first = TasksMdWriter.Apply(Sample, "SF-101", TrackerSection.Completed, "Completed 2026-09-23.");
        Assert.NotNull(first);
        Assert.Null(TasksMdWriter.Apply(first!, "SF-101", TrackerSection.Completed, "Completed 2026-09-23."));

        var reblocked = TasksMdWriter.Apply(first!, "SF-101", TrackerSection.Blocked, "Blocked: something else.");
        Assert.NotNull(reblocked);
    }

    [Fact]
    public void Unknown_item_id_is_a_no_op()
    {
        Assert.Null(TasksMdWriter.Apply(Sample, "SF-999", TrackerSection.Completed, null));
    }

    [Fact]
    public void Missing_target_section_heading_is_a_no_op()
    {
        const string noCompletedHeading = """
            ## Next up

            - [ ] **SF-100 — First item**
            """;
        Assert.Null(TasksMdWriter.Apply(noCompletedHeading, "SF-100", TrackerSection.Completed, "evidence"));
    }

    [Fact]
    public void Result_still_parses_cleanly_after_several_sequential_moves()
    {
        var content = Sample;
        content = TasksMdWriter.Apply(content, "SF-100", TrackerSection.InProgress, null)!;
        content = TasksMdWriter.Apply(content, "SF-100", TrackerSection.Completed, "Completed 2026-09-23: verified.")!;
        content = TasksMdWriter.Apply(content, "SF-101", TrackerSection.Blocked, "Blocked: needs a human decision.")!;

        var items = TasksMdParser.Parse(content);
        Assert.Equal(TrackerSection.Completed, items.Single(i => i.Id == "SF-100").Section);
        Assert.Equal(TrackerSection.Blocked, items.Single(i => i.Id == "SF-101").Section);
        Assert.Equal(TrackerSection.Completed, items.Single(i => i.Id == "SF-050").Section);
    }
}
