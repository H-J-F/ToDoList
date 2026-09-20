using ToDoList.Core;

namespace ToDoList.Tests;
public sealed class ContentAndDateTests
{
    [Fact] public void RichContentRoundTripsFormattingEmojiAndLinks()
    {
        var content = new RichContent(1, [new([new("中文 👩‍💻❤️", true, true, true, "#123ABC", "https://example.com")]), new([new("第二行")])]);
        var result = RichContent.Parse(content.ToJson());
        Assert.Equal(content.PlainText, result.PlainText); Assert.Equal(content.Paragraphs[0].Runs[0], result.Paragraphs[0].Runs[0]);
    }
    [Theory][InlineData("javascript:alert(1)")][InlineData("file:///c:/windows")][InlineData("not a url")]
    public void UnsupportedLinksAreRejected(string url)
    {
        var content = new RichContent(1, [new([new("链接", Link: url)])]);
        Assert.Throws<InvalidDataException>(() => RichContent.Parse(content.ToJson()));
    }
    [Fact] public void WeekBeginsMondayAndMonthCrossesYear()
    {
        var zone = TimeZoneInfo.Utc; var sunday = new DateTime(2026, 9, 20);
        var week = DateRanges.For(TaskFilter.Week, sunday, zone);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), week.From);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), week.Until);
        var month = DateRanges.For(TaskFilter.Month, new DateTime(2026, 12, 31), zone);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), month.Until);
    }
    [Fact] public void TodayHandlesDaylightSavingAndHalfOpenBoundaries()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var range = DateRanges.For(TaskFilter.Today, new DateTime(2026, 3, 8), zone);
        Assert.Equal(23 * 3600000L, range.Until - range.From);
        var q = new TaskQuery(null, TaskFilter.Today, TaskSort.Created, range.From, range.Until);
        var item = new TodoItem("id", null, "", "", TodoStatus.Completed, range.From!.Value, 0, 0, 1);
        Assert.True(q.Matches(item)); Assert.False(q.Matches(item with { CreatedAt = range.Until!.Value }));
    }
}
