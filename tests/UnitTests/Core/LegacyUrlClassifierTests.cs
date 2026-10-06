using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.UnitTests.Core;

public class LegacyUrlClassifierTests
{
    [Theory]
    [InlineData("/", LegacyUrlClass.Home)]
    [InlineData("", LegacyUrlClass.Home)]
    [InlineData("/?p=945", LegacyUrlClass.QueryString)]
    [InlineData("/?feed=rss2", LegacyUrlClass.QueryString)]
    [InlineData("/2008/07/the-onion-architecture-part-1/", LegacyUrlClass.Post)]
    [InlineData("/2008/07/the-onion-architecture-part-1", LegacyUrlClass.Post)]
    [InlineData("/2008/07/The-Onion-Architecture-Part-1/", LegacyUrlClass.Post)]
    [InlineData("/2008/07/the-onion-architecture-part-1/feed/", LegacyUrlClass.Feed)]
    [InlineData("/2008/07/the-onion-architecture-part-1/amp/", LegacyUrlClass.PostSubPath)]
    [InlineData("/2008/", LegacyUrlClass.DateArchive)]
    [InlineData("/2008/07/", LegacyUrlClass.DateArchive)]
    [InlineData("/2008/07/29/", LegacyUrlClass.DateArchive)]
    [InlineData("/2008/07/29/page/2/", LegacyUrlClass.DateArchive)]
    [InlineData("/2008/page/3/", LegacyUrlClass.DateArchive)]
    [InlineData("/page/2/", LegacyUrlClass.Pagination)]
    [InlineData("/tag/agile/", LegacyUrlClass.Taxonomy)]
    [InlineData("/category/blog/page/2/", LegacyUrlClass.Taxonomy)]
    [InlineData("/author/jeffreypalermo/", LegacyUrlClass.Taxonomy)]
    [InlineData("/tag/agile/feed/", LegacyUrlClass.Feed)]
    [InlineData("/feed/", LegacyUrlClass.Feed)]
    [InlineData("/feed/atom/", LegacyUrlClass.Feed)]
    [InlineData("/comments/feed/", LegacyUrlClass.Feed)]
    [InlineData("/wp-sitemap.xml", LegacyUrlClass.Sitemap)]
    [InlineData("/wp-sitemap-posts-post-1.xml", LegacyUrlClass.Sitemap)]
    [InlineData("/wp-content/uploads/2018/06/a.png", LegacyUrlClass.Media)]
    [InlineData("/blog/the-onion-architecture-part-2/", LegacyUrlClass.GraffitiBlogSlug)]
    [InlineData("/blogs/jeffrey.palermo/archive/2005/09/13/131914.aspx", LegacyUrlClass.CommunityServer)]
    [InlineData("/photos/jeffrey.palermo/images/158344/original.aspx", LegacyUrlClass.CommunityServer)]
    [InlineData("/default.aspx", LegacyUrlClass.CommunityServer)]
    [InlineData("/wp-admin/", LegacyUrlClass.WordPressSystem)]
    [InlineData("/wp-login.php?redirect_to=x", LegacyUrlClass.WordPressSystem)]
    [InlineData("/wp-json/wp/v2/posts", LegacyUrlClass.WordPressSystem)]
    [InlineData("/xmlrpc.php", LegacyUrlClass.WordPressSystem)]
    [InlineData("/the-onion-architecture-part-1-3/", LegacyUrlClass.TopLevelSlug)]
    [InlineData("/about/", LegacyUrlClass.TopLevelSlug)]
    [InlineData("/a/b/c", LegacyUrlClass.Other)]
    public void ClassifiesEachKindOfLegacyUrl(string url, LegacyUrlClass expected) =>
        Assert.Equal(expected, LegacyUrlClassifier.Classify(url));
}
