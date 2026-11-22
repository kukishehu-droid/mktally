using Tally.Infrastructure.Ingestion.Http;

namespace Tally.Infrastructure.Tests.Ingestion;

public class RobotsTxtTests
{
    [Fact]
    public void Wildcard_group_applies_when_no_specific_group()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /admin\n", "TallyBot");
        Assert.True(r.IsAllowed("/?org=3&page=1"));
        Assert.False(r.IsAllowed("/admin/login"));
    }

    [Fact]
    public void Specific_group_overrides_wildcard()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /\n\nUser-agent: tallybot\nAllow: /\n", "TallyBot");
        Assert.True(r.IsAllowed("/anything"));
    }

    [Fact]
    public void Longest_match_wins_and_allow_wins_ties()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /p\nAllow: /prices\nDisallow: /prices/old\n", "TallyBot");
        Assert.True(r.IsAllowed("/prices/today"));
        Assert.False(r.IsAllowed("/prices/old/1"));
        Assert.False(r.IsAllowed("/promo"));
    }

    [Fact]
    public void Wildcards_and_end_anchor()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /*?search=*x\nDisallow: /*.pdf$\n", "TallyBot");
        Assert.False(r.IsAllowed("/?search=xx"));
        Assert.True(r.IsAllowed("/?search=&page=1"));
        Assert.False(r.IsAllowed("/2026/11/20/73.pdf"));
        Assert.True(r.IsAllowed("/2026/11/20/73.pdf?v=1"));
    }

    [Fact]
    public void Wildcard_groups_combine_and_named_bots_do_not_leak()
    {
        // Shape of kipper.mk/robots.txt: two "*" groups, a ClaudeBot block in between.
        const string robots = "User-agent: *\nAllow: /\nUser-agent: ClaudeBot\nDisallow: /\n" +
                              "User-agent: *\r\nDisallow: /wp-admin/\r\nAllow: /wp-admin/admin-ajax.php\r\n";
        var tally = RobotsTxt.Parse(robots, "TallyBot");
        Assert.True(tally.IsAllowed("/mk/kipper-131struge/"));
        Assert.True(tally.IsAllowed("/wp-admin/admin-ajax.php"));
        Assert.False(tally.IsAllowed("/wp-admin/options.php"));

        var claudeBot = RobotsTxt.Parse(robots, "ClaudeBot");
        Assert.False(claudeBot.IsAllowed("/mk/kipper-131struge/"));
    }

    [Fact]
    public void Empty_disallow_allows_everything()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow:\n", "TallyBot");
        Assert.True(r.IsAllowed("/"));
    }
}
