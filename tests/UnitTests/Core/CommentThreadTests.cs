using JeffreyPalermo.Core.Content;
using static JeffreyPalermo.UnitTests.Core.ContentBuilder;

namespace JeffreyPalermo.UnitTests.Core;

public class CommentThreadTests
{
    private static Comment At(int id, int day, int parent = 0) => Comment(id, parent) with { Date = new DateTime(2009, 1, day) };

    [Fact]
    public void ListsTopLevelCommentsOldestFirst()
    {
        var thread = CommentThread.Build([At(3, day: 20), At(1, day: 13), At(2, day: 14)]);

        Assert.Equal([1, 2, 3], thread.Select(n => n.Comment.Id));
        Assert.All(thread, node => Assert.Empty(node.Replies));
    }

    [Fact]
    public void PutsEachReplyUnderTheCommentItAnswers()
    {
        var thread = CommentThread.Build([At(1, day: 13), At(2, day: 14), At(3, day: 15, parent: 1), At(4, day: 16, parent: 3), At(5, day: 17, parent: 1)]);

        Assert.Equal([1, 2], thread.Select(n => n.Comment.Id));
        Assert.Equal([3, 5], thread[0].Replies.Select(n => n.Comment.Id));
        Assert.Equal(4, Assert.Single(thread[0].Replies[0].Replies).Comment.Id);
        Assert.Empty(thread[1].Replies);
    }

    [Fact]
    public void CommentsOfTheSameMomentKeepTheirWordPressOrder() =>
        Assert.Equal([7, 9], CommentThread.Build([At(9, day: 13), At(7, day: 13)]).Select(n => n.Comment.Id));

    [Fact]
    public void APostWithoutCommentsHasAnEmptyThread() =>
        Assert.Empty(CommentThread.Build([]));
}
