using MetroDom.Core.Setlist;

namespace MetroDom.Core.Tests;

public sealed class SetQueueTests
{
    private static SetQueue Queue(params string[] titles)
    {
        var queue = new SetQueue();
        queue.Replace(titles.Select(t => (t, t)));
        return queue;
    }

    private static string[] Titles(SetQueue queue) => [.. queue.Snapshot().Songs.Select(s => s.Title)];

    [Fact]
    public void BeforeTheSet_EverythingCanChange()
    {
        var queue = Queue("a", "b");
        Assert.Equal(-1, queue.Snapshot().Current);
        Assert.Equal(0, queue.Snapshot().EditableFrom);

        Assert.True(queue.TryAdd("c", "c", null, at: 0, out _, out var error), error);
        Assert.Equal(["c", "a", "b"], Titles(queue));
    }

    [Fact]
    public void OnlyWhatsComingCanChange()
    {
        var queue = Queue("a", "b", "c", "d");
        queue.Cue(0);
        queue.Advance(); // b is playing (or waiting for start)
        var songs = queue.Snapshot().Songs;
        Assert.Equal(2, queue.Snapshot().EditableFrom);

        Assert.False(queue.TryRemove(songs[1].Id, out var error));
        Assert.Contains("skip it with next", error, StringComparison.Ordinal);
        Assert.False(queue.TryRemove(songs[0].Id, out _)); // played
        Assert.False(queue.TryMove(songs[3].Id, 1, out error));
        Assert.Contains("to must be 2-3", error, StringComparison.Ordinal);
        Assert.False(queue.TryAdd("x", "x", null, at: 1, out _, out error));
        Assert.Contains("at must be 2-4", error, StringComparison.Ordinal);

        Assert.True(queue.TryMove(songs[3].Id, 2, out error), error);
        Assert.Equal(["a", "b", "d", "c"], Titles(queue));
        Assert.True(queue.TryRemove(songs[2].Id, out error), error);
        Assert.Equal(["a", "b", "d"], Titles(queue));
        Assert.Equal("b", queue.Now?.Title);
    }

    [Fact]
    public void IdsStayPut_WhileIndexesMove()
    {
        var queue = Queue("a", "b", "c");
        var c = queue.Snapshot().Songs[2];
        Assert.True(queue.TryMove(c.Id, 0, out _));
        Assert.Equal(c, queue.Snapshot().Songs[0]);
        Assert.Equal(3, queue.Snapshot().Songs.Select(s => s.Id).Distinct().Count());
        Assert.False(queue.TryRemove("nope", out var error));
        Assert.Contains("no song nope", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunDryQueue_PlaysTheNextSongAdded()
    {
        var queue = Queue("a");
        queue.Cue(0);
        Assert.Null(queue.Advance());
        Assert.True(queue.IsDry);
        Assert.Null(queue.Now);

        Assert.True(queue.TryAdd("b", "b", "table 4", at: null, out var added, out _));
        Assert.False(queue.IsDry);
        Assert.Equal(added, queue.Now);
        Assert.Equal("table 4", queue.Now!.AddedBy);
    }

    [Fact]
    public void OldHistoryIsLetGo_ButTheCountStaysTrue()
    {
        var queue = Queue([.. Enumerable.Range(1, 30).Select(i => $"s{i}")]);
        queue.Cue(0);
        for (var i = 0; i < 25; i++)
        {
            queue.Advance();
        }

        var set = queue.Snapshot();
        Assert.Equal("s26", set.Now?.Title);
        Assert.Equal(SetQueue.MaxHistory, set.Current);
        Assert.Equal(5, set.Dropped);
        Assert.Equal(26, set.Current + 1 + set.Dropped);
        Assert.Equal(30, set.Songs.Count + set.Dropped);
    }

    [Fact]
    public void TheQueueHasACeiling()
    {
        var queue = Queue([.. Enumerable.Range(0, SetQueue.MaxSongs).Select(i => $"s{i}")]);
        Assert.False(queue.TryAdd("x", "x", null, null, out _, out var error));
        Assert.Contains("full", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryChange_IsCountedAndAnnounced()
    {
        var queue = Queue("a", "b");
        var changes = 0;
        queue.Changed += () => changes++;
        var before = queue.Snapshot().Version;

        queue.Cue(0);
        queue.TryAdd("c", "c", null, null, out var c, out _);
        queue.TryMove(c.Id, 1, out _);
        queue.TryRemove(c.Id, out _);
        queue.Advance();
        queue.TryRemove("missing", out _); // a refusal changes nothing

        Assert.Equal(5, changes);
        Assert.Equal(before + 5, queue.Snapshot().Version);
    }
}
