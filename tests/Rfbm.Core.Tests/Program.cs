using Rfbm.Core.Models;
using Rfbm.Core.Parsing;
using Rfbm.Tests;

var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");

TestAssert.Equal(PostStatus.Downloaded, PostStatus.NotDownloaded.Next());
TestAssert.Equal(PostStatus.LinkInvalid, PostStatus.Downloaded.Next());
TestAssert.Equal(PostStatus.NotDownloaded, PostStatus.LinkInvalid.Next());

TestAssert.Equal("未下载", PostStatus.NotDownloaded.DisplayName());
TestAssert.Equal("已下载", PostStatus.Downloaded.DisplayName());
TestAssert.Equal("链接失效", PostStatus.LinkInvalid.DisplayName());

var queryKey = ThreadKey.Parse(
    "https://primary.example.test/forum.php?mod=viewthread&tid=2730872&page=2",
    "example-forum");
var prettyKey = ThreadKey.Parse(
    "https://primary.example.test/thread-2730872-1-1.html",
    "example-forum");
var mirrorKey = ThreadKey.Parse(
    "https://mirror.example.test/thread-2730872-9-1.html",
    "example-forum");
TestAssert.Equal(queryKey, prettyKey);
TestAssert.Equal(queryKey, mirrorKey);
TestAssert.Equal("example-forum:2730872", queryKey.ToString());
TestAssert.Throws<FormatException>(() => ThreadKey.Parse("https://example.test/forum.php?mod=forumdisplay"));

var unavailable = Observed<string>.Unobservable();
var empty = Observed<string>.Empty();
var value = Observed<string>.From("archive-pass");
TestAssert.Equal(ObservationKind.Unobservable, unavailable.Kind);
TestAssert.Equal(ObservationKind.ObservedEmpty, empty.Kind);
TestAssert.Equal(ObservationKind.ObservedValue, value.Kind);
TestAssert.Equal("archive-pass", value.Value);
TestAssert.Throws<InvalidOperationException>(() => _ = empty.Value);

var favoritesHtml = File.ReadAllText(Path.Combine(fixtureRoot, "favorites-page-1.html"));
var favoritePage = DiscuzPageParser.ParseFavorites(
    favoritesHtml,
    new Uri("https://primary.example.test/home.php?mod=space&do=favorite&uid=42&view=me&type=all&page=1"),
    "example-forum");
TestAssert.Equal(2, favoritePage.Items.Count);
TestAssert.Equal("2730872", favoritePage.Items[0].Key.ThreadId);
TestAssert.Equal("第一帖", favoritePage.Items[0].Title);
TestAssert.Equal("2026-05-13 10:20", favoritePage.Items[0].RawFavoriteTime);
TestAssert.Equal(false, favoritePage.Items[0].FavoriteTime is null);
TestAssert.Equal("昨天 23:10", favoritePage.Items[1].RawFavoriteTime);
TestAssert.Equal<DateTimeOffset?>(null, favoritePage.Items[1].FavoriteTime);
TestAssert.Equal(62, favoritePage.Pagination.MaxPage);
TestAssert.Equal(62, favoritePage.Pagination.PageUrls.Count);
TestAssert.Equal(
    "https://primary.example.test/home.php?mod=space&do=favorite&uid=42&view=me&type=all&page=2",
    favoritePage.Pagination.NextUrl);
TestAssert.Equal(
    "https://primary.example.test/home.php?mod=space&do=favorite&uid=42&view=me&type=all&page=62",
    favoritePage.Pagination.PageUrls[^1]);

var emptyFavorites = DiscuzPageParser.ParseFavorites(
    "<main id='ct'>您还没有添加任何收藏</main><footer id='ft'><a href='thread-999-1-1.html'>帮助</a></footer>",
    new Uri("https://primary.example.test/home.php?do=favorite&page=1"),
    "example-forum");
TestAssert.Equal(0, emptyFavorites.Items.Count);
var loginHtml = File.ReadAllText(Path.Combine(fixtureRoot, "login-page.html"));
TestAssert.Throws<InvalidPageException>(() => DiscuzPageParser.ParseFavorites(
    loginHtml,
    new Uri("https://primary.example.test/home.php?mod=space&do=favorite&page=1"),
    "example-forum"));

var threadHtml = File.ReadAllText(Path.Combine(fixtureRoot, "thread-page.html"));
var favoriteReference = favoritePage.Items[0];
var post = DiscuzPageParser.ParseThread(
    threadHtml,
    new Uri("https://primary.example.test/forum.php?mod=viewthread&tid=2730872"),
    "example-forum",
    favoriteReference,
    new Uri("https://mirror.example.test/forum.php?mod=viewthread&tid=2730872"));
TestAssert.Equal(queryKey, post.Key);
TestAssert.Equal("[游戏] 示例收藏标题", post.Title);
TestAssert.Equal("游戏", post.Type);
TestAssert.Equal("2026-05-13 10:20", post.RawFavoriteTime);
TestAssert.Equal(1234, post.VisitCount.Value);
TestAssert.Equal(88, post.DownloadCount.Value);
TestAssert.Equal(9, post.FavoriteCount.Value);
TestAssert.Equal("ARCHIVE-446336", post.ExtractPassword.Value);
TestAssert.Equal("wrong-code", post.AccessCode.Value);
TestAssert.Equal("generic-secret", DiscuzPageParser.ExtractPassword("密码：generic-secret"));
AssertContains(post.BodyText.Value, "这是帖子正文");
TestAssert.Equal(9, post.Images.Count);
TestAssert.Equal(9, post.Images.Select(image => image.SourceUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count());
TestAssert.Equal(
    "https://mirror.example.test/remote/data/attachment/forum/real-1.png",
    post.Images[0].SourceUrl);
TestAssert.Equal(3, post.ResourceLinks.Count);
TestAssert.Equal("https://pan.example.test/s/abc", post.ResourceLinks[0].Url);
AssertContains(post.ResourceLinks[0].ContextText, "提取码: 2jsu");
TestAssert.Equal("magnet:?xt=urn:btih:12345", post.ResourceLinks[1].Url);
TestAssert.Equal(
    "https://mirror.example.test/forum.php?mod=attachment&aid=preview1",
    post.ResourceLinks[2].Url);

var discuzX34Html = File.ReadAllText(Path.Combine(fixtureRoot, "discuz-x34-shape.html"));
var discuzX34Post = DiscuzPageParser.ParseThread(
    discuzX34Html,
    new Uri("https://primary.example.test/forum.php?mod=viewthread&tid=2730999"),
    "example-forum");
TestAssert.Equal("[工具] 示例资源标题", discuzX34Post.Title);
TestAssert.Equal("工具", discuzX34Post.Type);
TestAssert.Equal(4_333_000, discuzX34Post.VisitCount.Value);
TestAssert.Equal("这是脱敏的 Discuz X3.4 结构样例。 正文只用于验证首楼选择器和计数提取。", discuzX34Post.BodyText.Value);

var valid = PageValidation.ValidateThread(
    threadHtml,
    new Uri("https://mirror.example.test/thread-2730872-1-1.html"),
    queryKey);
TestAssert.Equal(true, valid.IsValid);

var login = PageValidation.ValidateThread(
    "<html><title>用户登录</title><form id='loginform'><input name='password'></form></html>",
    new Uri("https://primary.example.test/member.php?mod=logging"),
    queryKey);
TestAssert.Equal(PageRejectionReason.LoginRequired, login.Reason);
var bareLoginForm = PageValidation.ValidateCollection(
    "<html><form id='loginform'></form></html>",
    new Uri("https://primary.example.test/home.php?do=favorite"));
TestAssert.Equal(PageRejectionReason.LoginRequired, bareLoginForm.Reason);

var challenge = PageValidation.ValidateThread(
    "<html><title>Just a moment...</title><div>Checking your browser before accessing</div></html>",
    new Uri("https://primary.example.test/thread-2730872-1-1.html"),
    queryKey);
TestAssert.Equal(PageRejectionReason.Challenge, challenge.Reason);

var error = PageValidation.ValidateThread(
    "<html><title>503 Service Unavailable</title><div>数据库错误，请稍后重试</div></html>",
    new Uri("https://primary.example.test/thread-2730872-1-1.html"),
    queryKey);
TestAssert.Equal(PageRejectionReason.ErrorPage, error.Reason);

var mismatch = PageValidation.ValidateThread(
    "<html><head><link rel='canonical' href='https://primary.example.test/thread-999999-1-1.html'></head><body><h1 id='thread_subject'>别的帖子</h1></body></html>",
    new Uri("https://primary.example.test/thread-2730872-1-1.html"),
    queryKey);
TestAssert.Equal(PageRejectionReason.IdentityMismatch, mismatch.Reason);
var missingContent = PageValidation.ValidateThread(
    "<html><body>普通导航页</body></html>",
    new Uri("https://primary.example.test/thread-2730872-1-1.html"),
    queryKey);
TestAssert.Equal(PageRejectionReason.MissingContent, missingContent.Reason);

TestAssert.Throws<InvalidPageException>(() => DiscuzPageParser.ParseThread(
    loginHtml,
    new Uri("https://primary.example.test/thread-2730872-1-1.html"),
    "example-forum"));

Console.WriteLine("Rfbm.Core.Tests passed.");

static void AssertContains(string value, string expected)
{
  if (!value.Contains(expected, StringComparison.Ordinal))
  {
    throw new InvalidOperationException($"Expected '{value}' to contain '{expected}'.");
  }
}
