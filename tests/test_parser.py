from forum_bookmark_manager.parser import (
    detect_project_type,
    extract_password,
    parse_favorite_page,
    parse_post_page,
)


def test_parse_favorite_page_extracts_items_and_next_url():
    html = """
    <html><body>
      <div class="fav-row">
        <a href="forum.php?mod=viewthread&tid=1001">第一帖</a>
        <span>收藏于 2026-05-13 10:20</span>
      </div>
      <div class="fav-row">
        <a href="/thread-1002-1-1.html">第二帖</a>
        <span>收藏时间: 2026-05-12</span>
      </div>
      <div class="pg"><a class="nxt" href="home.php?mod=space&page=2">下一页</a></div>
    </body></html>
    """

    page = parse_favorite_page(html, "https://example.test/home.php?page=1")

    assert [item.url for item in page.items] == [
        "https://example.test/forum.php?mod=viewthread&tid=1001",
        "https://example.test/thread-1002-1-1.html",
    ]
    assert page.items[0].favorite_time == "2026-05-13 10:20"
    assert page.items[1].favorite_time == "2026-05-12"
    assert page.next_url == "https://example.test/home.php?mod=space&page=2"


def test_parse_favorite_page_discovers_all_numbered_pagination_urls():
    html = """
    <html><body>
      <ul class="el">
        <li id="favorite_li_1">
          <a href="forum.php?mod=viewthread&tid=1001">收藏帖</a>
          <span>2026-05-13</span>
        </li>
      </ul>
      <div class="pg">
        <strong>1</strong>
        <a href="home.php?mod=space&uid=1000000&do=favorite&type=all&page=2">2</a>
        <a href="home.php?mod=space&uid=1000000&do=favorite&type=all&page=3">3</a>
        <a href="home.php?mod=space&uid=1000000&do=favorite&type=all&page=60">60</a>
        <a class="nxt" href="home.php?mod=space&uid=1000000&do=favorite&type=all&page=2">下一页</a>
      </div>
    </body></html>
    """

    page = parse_favorite_page(
        html,
        "https://example.test/home.php?mod=space&uid=1000000&do=favorite&type=all&page=1",
    )

    assert page.max_page == 60
    assert len(page.page_urls) == 60
    assert page.page_urls[0].endswith("page=1")
    assert page.page_urls[-1].endswith("page=60")


def test_parse_favorite_page_does_not_treat_footer_links_as_favorites_when_empty():
    html = """
    <html><body>
      <div id="ct">
        <p>您还没有添加任何收藏</p>
      </div>
      <div id="ft">
        <a href="/forum.php?mod=viewthread&tid=307244">新手帮助</a>
        <a href="/thread-2163-1-1.html">地址发布</a>
      </div>
    </body></html>
    """

    page = parse_favorite_page(html, "https://example.test/home.php?mod=space&do=favorite&page=1")

    assert page.items == []
    assert page.page_urls == []


def test_parse_favorite_page_keeps_relative_favorite_time_from_real_list_rows():
    html = """
    <html><body>
      <div id="ct">
        <ul class="el">
          <li id="favorite_li_4101">
            <input type="checkbox" name="delete[]" value="4101">
            <a href="forum.php?mod=viewthread&tid=4101">被单男哥哥慢慢玩弄越夹越淫荡</a>
            <span>3 小时前</span>
            <a href="home.php?mod=spacecp&ac=favorite&op=delete&favid=4101">删除</a>
          </li>
          <li id="favorite_li_4102">
            <a href="/thread-4102-1-1.html">九儿橙 5月8日新作</a>
            <span>昨天 23:10</span>
          </li>
        </ul>
      </div>
    </body></html>
    """

    page = parse_favorite_page(html, "https://example.test/home.php?mod=space&do=favorite&page=1")

    assert [item.favorite_time for item in page.items] == ["3 小时前", "昨天 23:10"]
    assert [item.title for item in page.items] == [
        "被单男哥哥慢慢玩弄越夹越淫荡",
        "九儿橙 5月8日新作",
    ]


def test_extract_password_detects_common_chinese_labels():
    assert extract_password("解压密码：abcd1234 请勿在线解压") == "abcd1234"
    assert extract_password("解压码：archive-446336") == "archive-446336"
    assert extract_password("壓縮密碼: 示例论坛") == "示例论坛"
    assert extract_password("提取码: wxyz  解压密码: 9999") == "9999"
    assert extract_password("password = pass-2026") == "pass-2026"


def test_detect_project_type_uses_category_then_keywords():
    assert detect_project_type("任意标题", "正文", ["论坛", "游戏专区"]) == "游戏"
    assert detect_project_type("精品国产自拍合集", "", []) == "国产自拍"
    assert detect_project_type("没有明显分类", "普通正文", []) == "未分类"
    assert detect_project_type("任意标题", "正文", ["P站女优"]) == "P站女优"


def test_parse_post_page_extracts_structured_post_data():
    html = """
    <html>
      <head><title>备用标题 - 示例论坛</title></head>
      <body>
        <div id="pt"><a>论坛</a><a>游戏专区</a></div>
        <h1 id="thread_subject">[游戏] 示例收藏标题</h1>
        <div class="hm">查看: 1,234 | 回复: 5</div>
        <span>下载次数 88</span>
        <span>收藏 9</span>
        <td class="t_f">
          <p>这是帖子正文。解压密码：abcd1234</p>
          <p>百度网盘下载：<a href="https://pan.baidu.com/s/abc">主程序</a> 提取码 efgh</p>
          <p>备用链接 <a href="magnet:?xt=urn:btih:12345">磁力下载</a></p>
          <img src="/images/post-1.jpg" width="640" height="480">
          <img src="/static/avatar.png" width="32" height="32">
          <img src="https://img.example.test/post-2.jpg">
        </td>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://example.test/thread-1001-1-1.html",
        favorite_url="https://example.test/home.php?page=1",
        favorite_time="2026-05-13",
    )

    assert post.title == "[游戏] 示例收藏标题"
    assert post.project_type == "游戏"
    assert post.post_url == "https://example.test/thread-1001-1-1.html"
    assert post.favorite_url == "https://example.test/home.php?page=1"
    assert post.favorite_time == "2026-05-13"
    assert post.extract_password == "abcd1234"
    assert post.visit_count == 1234
    assert post.download_count == 88
    assert post.favorite_count == 9
    assert [image.source_url for image in post.images] == [
        "https://example.test/images/post-1.jpg",
        "https://img.example.test/post-2.jpg",
    ]
    assert [link.url for link in post.download_links] == [
        "https://pan.baidu.com/s/abc",
        "magnet:?xt=urn:btih:12345",
    ]
    assert post.download_links[0].label == "主程序"
    assert "百度网盘下载" in post.download_links[0].context_text


def test_parse_post_page_uses_user_selected_regions():
    html = """
    <html>
      <body>
        <h1>Navigation Title</h1>
        <div class="crumb">Forum / Games</div>
        <div class="wrong-body">
          <img src="/avatar.jpg" width="640" height="480">
          <a href="https://example.test/help">Help</a>
        </div>
        <section id="selected-title">Selected Post Title</section>
        <section id="selected-body">Selected body text without the noisy navigation.</section>
        <section id="selected-password">zip-password-777</section>
        <section id="selected-images">
          <img src="/real-1.jpg" width="640" height="480">
          <img file="/real-2.webp" width="640" height="480">
        </section>
        <section id="selected-downloads">
          Primary cloud:
          <a href="https://cloud.example.test/file/abc">Cloud file</a>
          Mirror:
          <a href="https://mirror.example.test/file/abc">Mirror file</a>
        </section>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://example.test/thread-1.html",
        selector_profile={
            "title": "#selected-title",
            "body": "#selected-body",
            "password": "#selected-password",
            "images": "#selected-images",
            "download_links": "#selected-downloads",
            "project_type": ".crumb",
        },
    )

    assert post.title == "Selected Post Title"
    assert post.body_text == "Selected body text without the noisy navigation."
    assert post.extract_password == "zip-password-777"
    assert post.project_type == "游戏"
    assert [image.source_url for image in post.images] == [
        "https://example.test/real-1.jpg",
        "https://example.test/real-2.webp",
    ]
    assert [link.url for link in post.download_links] == [
        "https://cloud.example.test/file/abc",
        "https://mirror.example.test/file/abc",
    ]


def test_parse_post_page_falls_back_when_saved_selectors_point_to_wrong_regions():
    html = """
    <html>
      <body>
        <div id="pt"><a>首页</a><a>视频资源下载</a><a>欧美视频下载</a></div>
        <h1 id="thread_subject">真实帖子标题</h1>
        <div id="postlist">
          <div id="post_100">
            <table id="pid100"><tr><td class="plc">
              <div class="pcb">
                <div class="typeoption">
                  <table class="cgtl">
                    <tr><th>解压密码:</th><td id="marked-downloads">sample-pass</td></tr>
                  </table>
                </div>
                <div id="marked-title">
                  📥 下载链接
                  <p>百度盘: <a href="https://pan.baidu.com/s/abc?pwd=2jsu">点击下载</a> 提取码: 2jsu</p>
                  <p>售价：1软妹币 已购人数：4500</p>
                </div>
                <div class="t_fsz">
                  <div id="marked-images">
                    <img src="/static/image/common/none.gif" file="/remote/data/attachment/forum/real-1.png" zoomfile="/remote/data/attachment/forum/real-1.png" width="600">
                  </div>
                </div>
              </div>
            </td></tr></table>
          </div>
        </div>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://primary.example.test/forum.php?mod=viewthread&tid=2730872",
        selector_profile={
            "title": "#marked-title",
            "download_links": "#marked-downloads",
            "images": "#marked-images",
        },
    )

    assert post.title == "真实帖子标题"
    assert post.extract_password == "sample-pass"
    assert [link.url for link in post.download_links] == ["https://pan.baidu.com/s/abc?pwd=2jsu"]
    assert [image.source_url for image in post.images] == [
        "https://primary.example.test/remote/data/attachment/forum/real-1.png"
    ]


def test_parse_post_page_falls_back_to_first_floor_images_when_marked_region_has_none():
    html = """
    <html>
      <body>
        <h1 id="thread_subject">图片回退测试</h1>
        <div id="postlist">
          <div id="post_100">
            <div class="pcb">
              <div id="bad-images">这里不是图片区</div>
              <div class="pattl">
                <img src="/static/image/common/none.gif" file="/remote/data/attachment/forum/real-1.png" width="600">
              </div>
            </div>
          </div>
        </div>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://primary.example.test/forum.php?mod=viewthread&tid=2730872",
        selector_profile={"images": "#bad-images"},
    )

    assert [image.source_url for image in post.images] == [
        "https://primary.example.test/remote/data/attachment/forum/real-1.png"
    ]


def test_parse_post_page_uses_text_near_selected_images_when_body_is_not_marked():
    html = """
    <html>
      <body>
        <td class="t_f">Noisy text from another post should not win.</td>
        <div class="pcb">
          <div class="typeoption">Download metadata should stay nearby but not be the only source.</div>
          <div class="t_fsz">
            <p>Real post intro beside the image area.</p>
            <div id="selected-images" class="pattl">
              <p>Image area explanation text.</p>
              <img src="/real.jpg" width="640" height="480">
            </div>
          </div>
        </div>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://example.test/thread-1.html",
        selector_profile={"images": "#selected-images"},
    )

    assert "Real post intro beside the image area." in post.body_text
    assert "Image area explanation text." in post.body_text
    assert "Noisy text from another post" not in post.body_text


def test_parse_post_page_uses_first_floor_discuz_resource_blocks():
    html = """
    <html>
      <body>
        <div id="pt">
          <a>首页</a><a>视频资源下载</a><a>欧美视频下载</a>
        </div>
        <h1 id="thread_subject">资源标题</h1>
        <div id="postlist">
          <div id="post_100">
            <table id="pid100" class="plhin"><tr><td class="plc">
              <div class="pct">
                <div class="pcb">
                  <div class="typeoption">
                    <table class="cgtl">
                      <tr><th>下载方式:</th><td>百度盘</td></tr>
                      <tr><th>来源:</th><td>自行打包</td></tr>
                      <tr><th>文件数量:</th><td>167v</td></tr>
                      <tr><th>资源大小:</th><td>66.8G</td></tr>
                      <tr><th>解压密码:</th><td>sample-pass</td></tr>
                    </table>
                  </div>
                  <div class="download-box">
                    📥 下载链接
                    <p>百度盘: <a href="https://pan.baidu.com/s/abc?pwd=2jsu">点击下载</a> 提取码: 2jsu</p>
                  </div>
                  <div id="postmessage_100">首楼正文。</div>
                  <div class="pattl">
                    <a href="forum.php?mod=attachment&aid=preview1">下载附件</a>
                    <img src="/static/image/common/none.gif" file="/remote/data/attachment/forum/real-1.png" zoomfile="/remote/data/attachment/forum/real-1.png" width="600">
                    <img src="/static/image/common/none.gif" file="/remote/data/attachment/forum/real-2.gif" zoomfile="/remote/data/attachment/forum/real-2.gif" width="600">
                  </div>
                </div>
              </div>
            </td></tr></table>
          </div>
          <div id="post_101">
            <div class="pcb">
              <div id="postmessage_101">评论里的解压密码: wrong</div>
              <a href="https://pan.baidu.com/s/comment">评论链接</a>
            </div>
          </div>
        </div>
      </body>
    </html>
    """

    post = parse_post_page(html, post_url="https://primary.example.test/forum.php?mod=viewthread&tid=2730872")

    assert post.project_type == "欧美视频下载"
    assert post.extract_password == "sample-pass"
    assert [image.source_url for image in post.images] == [
        "https://primary.example.test/remote/data/attachment/forum/real-1.png",
        "https://primary.example.test/remote/data/attachment/forum/real-2.gif",
    ]
    assert [link.url for link in post.download_links] == [
        "https://pan.baidu.com/s/abc?pwd=2jsu",
    ]
    assert "提取码: 2jsu" in post.download_links[0].context_text
    assert "评论链接" not in post.body_text


def test_parse_post_page_extracts_password_from_typeoption_when_body_selector_is_narrow():
    html = """
    <html>
      <body>
        <h1 id="thread_subject">带资源表格的帖子</h1>
        <div id="postlist">
          <div id="post_100">
            <div class="pcb">
              <div class="typeoption">
                <table class="cgtl">
                  <tr><th>下载方式:</th><td>网盘</td></tr>
                  <tr><th>解压码:</th><td>ARCHIVE-446336</td></tr>
                </table>
              </div>
              <div id="postmessage_100">正文没有密码。</div>
            </div>
          </div>
        </div>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://primary.example.test/forum.php?mod=viewthread&tid=446336",
        selector_profile={"body": "#postmessage_100"},
    )

    assert post.extract_password == "ARCHIVE-446336"


def test_parse_post_page_extracts_images_from_lazy_render_attributes():
    html = """
    <html>
      <body>
        <h1 id="thread_subject">图片懒加载帖子</h1>
        <div id="postlist">
          <div id="post_100">
            <div class="pcb">
              <div class="pattl">
                <img src="/static/image/common/none.gif" data-original="/remote/data/attachment/forum/lazy-1.jpg" width="640" height="480">
                <img src="/static/image/common/none.gif" data-echo="/remote/data/attachment/forum/lazy-2.webp" width="640" height="480">
                <img src="/static/image/common/none.gif" srcset="/remote/data/attachment/forum/lazy-3.jpg 640w" width="640" height="480">
              </div>
            </div>
          </div>
        </div>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://primary.example.test/forum.php?mod=viewthread&tid=446336",
    )

    assert [image.source_url for image in post.images] == [
        "https://primary.example.test/remote/data/attachment/forum/lazy-1.jpg",
        "https://primary.example.test/remote/data/attachment/forum/lazy-2.webp",
        "https://primary.example.test/remote/data/attachment/forum/lazy-3.jpg",
    ]


def test_parse_post_page_uses_loaded_url_for_relative_media_without_changing_post_url():
    html = """
    <html>
      <body>
        <h1 id="thread_subject">mirror host media</h1>
        <div id="postlist">
          <div id="post_100">
            <div class="pcb">
              <div class="pattl">
                <img src="/remote/data/attachment/forum/real-1.jpg" width="640" height="480">
              </div>
              <p><a href="/forum.php?mod=attachment&aid=abc">attachment preview</a></p>
              <p><a href="/download/file.zip">download</a></p>
            </div>
          </div>
        </div>
      </body>
    </html>
    """

    post = parse_post_page(
        html,
        post_url="https://primary.example.test/forum.php?mod=viewthread&tid=2743445",
        content_base_url="https://mirror.example.test/forum.php?mod=viewthread&tid=2743445",
    )

    assert post.post_url == "https://primary.example.test/forum.php?mod=viewthread&tid=2743445"
    assert [image.source_url for image in post.images] == [
        "https://mirror.example.test/remote/data/attachment/forum/real-1.jpg"
    ]
    assert [link.url for link in post.download_links] == ["https://mirror.example.test/download/file.zip"]
