using LupaFiscal.Core.Crawling;

namespace LupaFiscal.Tests;

public class RobotsRulesTests
{
    [Fact]
    public void AllowAllAllowsEverything()
    {
        Assert.True(RobotsRules.AllowAll.IsAllowed("/pt/anything.pdf"));
        Assert.False(RobotsRules.AllowAll.BlocksEverything);
    }

    [Fact]
    public void DisallowAllBlocksEverything()
    {
        Assert.False(RobotsRules.DisallowAll.IsAllowed("/"));
        Assert.True(RobotsRules.DisallowAll.BlocksEverything);
    }

    [Fact]
    public void WildcardGroupAppliesWhenNoGroupNamesTheCrawler()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /private/\n\nUser-agent: otherbot\nDisallow: /", "lupafiscal");

        Assert.False(rules.IsAllowed("/private/a.pdf"));
        Assert.True(rules.IsAllowed("/public/a.pdf"));
    }

    [Fact]
    public void IgnoresALeadingByteOrderMark()
    {
        var rules = RobotsRules.Parse("\uFEFFUser-agent: *\nDisallow: /", "lupafiscal");

        Assert.False(rules.IsAllowed("/pt/a.pdf"));
    }

    [Fact]
    public void OwnGroupReplacesTheWildcardGroup()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /\n\nUser-agent: LupaFiscal\nDisallow: /docs/", "lupafiscal");

        Assert.True(rules.IsAllowed("/pt/page"));
        Assert.False(rules.IsAllowed("/docs/a.pdf"));
    }

    [Fact]
    public void LongestMatchWinsAndAllowWinsTies()
    {
        var rules = RobotsRules.Parse("""
            User-agent: *
            Disallow: /pt/
            Allow: /pt/informacao_fiscal/
            Disallow: /pt/informacao_fiscal/x
            Allow: /pt/informacao_fiscal/x
            """, "lupafiscal");

        Assert.False(rules.IsAllowed("/pt/outro"));
        Assert.True(rules.IsAllowed("/pt/informacao_fiscal/a.pdf"));
        Assert.True(rules.IsAllowed("/pt/informacao_fiscal/x.pdf"));
    }

    [Fact]
    public void WildcardsAndEndAnchorsMatch()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /*.pdf$\nDisallow: /*/_vti_bin/", "lupafiscal");

        Assert.False(rules.IsAllowed("/pt/Documents/PIV_1.pdf"));
        Assert.True(rules.IsAllowed("/pt/Documents/PIV_1.pdf?x=1"));
        Assert.False(rules.IsAllowed("/pt/cirs/_vti_bin/portalat/docs.svc/listdocs?id=42"));
        Assert.True(rules.IsAllowed("/pt/cirs/Pages/index.aspx"));
    }

    [Fact]
    public void PercentEncodingIsNormalisedOnBothSides()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /pt/Ficha Doutrinária", "lupafiscal");

        Assert.False(rules.IsAllowed("/pt/Ficha%20Doutrin%C3%A1ria%20-%20Proc.pdf"));
    }

    [Fact]
    public void CommentsAndEmptyDisallowAreIgnored()
    {
        var rules = RobotsRules.Parse("# comment\nUser-agent: * # all\nDisallow:\n", "lupafiscal");

        Assert.True(rules.IsAllowed("/anything"));
    }
}
