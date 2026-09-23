namespace Factory.Core;

/// <summary>Applies one tracker item's section transition to a <c>TASKS.md</c> document's text (SF-707) — a pure,
/// independently testable transform. The item's own already-written lines are relocated verbatim (never
/// reconstructed from parsed fields), so an item's hand-written prose, nested <c>Dependencies:</c>/<c>Scope:</c>/
/// <c>Acceptance:</c> bullets, and any other formatting survive a move untouched; the only things this ever
/// changes are the checkbox character (checked only in <see cref="TrackerSection.Completed"/>) and, optionally,
/// one new trailing nested bullet recording why the move happened (evidence for <see cref="TrackerSection.Completed"/>,
/// an unblock condition for <see cref="TrackerSection.Blocked"/>).</summary>
public static class TasksMdWriter
{
    /// <summary>Moves <paramref name="trackerItemId"/>'s item into <paramref name="targetSection"/>, appending
    /// <paramref name="note"/> as a new nested bullet if given. Returns <see langword="null"/> — a no-op, not an
    /// error — when the item cannot be found, when a canonical section heading this move needs is missing from
    /// the document, or when the item already sits in <paramref name="targetSection"/> (the note, if any, is
    /// compared too, so a repeated call with the same note is idempotent but a materially different one — e.g. a
    /// different failure reason on a later automatic block — still applies).</summary>
    public static string? Apply(string content, string trackerItemId, TrackerSection targetSection, string? note)
    {
        var lines = TasksMdParser.Normalize(content).ToList();
        var item = TasksMdParser.Parse(content).FirstOrDefault(i => i.Id == trackerItemId);
        if (item is null) return null;

        var noteLine = note is null ? null : $"  - {note}";
        if (item.Section == targetSection && (noteLine is null || lines.Skip(item.StartLine).Take(item.EndLineExclusive - item.StartLine).Contains(noteLine)))
            return null;

        var target = TasksMdParser.FindSectionRange(content, targetSection);
        if (target is null) return null;

        var block = lines.GetRange(item.StartLine, item.EndLineExclusive - item.StartLine);
        block[0] = SetChecked(block[0], targetSection == TrackerSection.Completed);
        if (noteLine is not null) block.Add(noteLine);

        // Remove the original block first, then insert at the target location — computed against the
        // already-shrunk line list when the removal was earlier in the file than the insertion point.
        lines.RemoveRange(item.StartLine, item.EndLineExclusive - item.StartLine);
        var insertAt = target.Value.InsertBeforeLine;
        if (item.StartLine < insertAt) insertAt -= item.EndLineExclusive - item.StartLine;
        // Insert directly before the next heading (or end of file), preceded by a blank separator line if the
        // immediately preceding line is not already blank and not the section's own heading line.
        while (insertAt > 0 && lines[insertAt - 1].Length == 0 && insertAt - 1 > 0 && lines[insertAt - 2].Length == 0) insertAt--;
        if (insertAt > 0 && lines[insertAt - 1].Length != 0) { lines.Insert(insertAt, ""); insertAt++; }
        lines.InsertRange(insertAt, block);
        if (insertAt + block.Count < lines.Count && lines[insertAt + block.Count].Length != 0) lines.Insert(insertAt + block.Count, "");

        return string.Join('\n', lines);
    }

    private static string SetChecked(string bulletLine, bool @checked) =>
        bulletLine.Length > 3 && bulletLine[0] == '-' && bulletLine[1] == ' ' && bulletLine[2] == '['
            ? $"{bulletLine[..3]}{(@checked ? 'x' : ' ')}{bulletLine[4..]}"
            : bulletLine;
}
