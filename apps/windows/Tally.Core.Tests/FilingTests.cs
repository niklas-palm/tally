namespace Tally.Tests;

/// <summary>Filing: the folder the owner usually files meetings with these people in, twice or more, over half the time.</summary>
public class FilingTests
{
    private static Meeting Make(string id, string[] people, string? folder = null, string state = "ready") =>
        new(id, state, People: people, FolderIds: folder is null ? null : [folder]);

    private static readonly List<Meeting> Filed =
        [Make("1", ["me", "anna"], "acme"), Make("2", ["erik"], "acme"), Make("3", ["anna"], "team"), Make("4", ["me"], "team")];

    [Fact]
    public void A_meeting_is_suggested_the_folder_its_people_are_usually_in()
    {
        var suggestion = Filing.Suggest(Make("new", ["me", "anna", "erik"]), Filed, "me");
        Assert.Equal("acme", suggestion?.Folder);
        Assert.Equal(["anna", "erik"], suggestion?.People);
    }

    [Fact]
    public void The_owner_a_tie_a_filed_meeting_or_one_not_ready_suggest_nothing()
    {
        Assert.Null(Filing.Suggest(Make("new", ["me"]), Filed, "me"));  // the owner is in every meeting
        Assert.Null(Filing.Suggest(Make("new", ["anna"]), Filed, "me"));  // a tie
        Assert.Null(Filing.Suggest(Make("new", ["erik"], "team"), Filed, "me"));
        Assert.Null(Filing.Suggest(Make("new", ["erik"], state: "processing"), Filed, "me"));
        Assert.Null(Filing.Suggest(Make("new", ["erik"]), Filed, "me"));  // once isn't usually
    }

    [Fact]
    public void A_meeting_in_two_folders_counts_for_both()
    {
        // Anna's in Acme and Team, so with Acme's two others, Acme leads.
        List<Meeting> both = [.. Filed, Make("5", ["anna"], "acme"), new("6", "ready", People: ["anna"], FolderIds: ["acme", "team"])];
        Assert.Equal("acme", Filing.Suggest(Make("new", ["anna"]), both, "me")?.Folder);
    }
}
