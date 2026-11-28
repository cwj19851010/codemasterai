namespace CodeMaster.WebApi.Models;

/// <summary>
/// 门户「联系我们」页面的展示数据，全部来自配置 Portal:Contact。
/// </summary>
public class PortalContactViewModel
{
    /// <summary>联系邮箱</summary>
    public string? Email { get; set; }

    /// <summary>QQ 群号（可直接展示，便于搜索加群）</summary>
    public string? QqGroupNumber { get; set; }

    /// <summary>QQ 群二维码图片地址（相对 /site/images/ 或绝对路径）</summary>
    public string? QqGroupQrImage { get; set; }

    /// <summary>微信群二维码图片地址（注意：群二维码 7 天过期，需定期更换）</summary>
    public string? WeChatQrImage { get; set; }

    /// <summary>微信群补充说明，例如「二维码失效请先加 QQ 群」</summary>
    public string? WeChatNote { get; set; }

    /// <summary>是否配置了任意一种即时联系方式</summary>
    public bool HasChatChannel =>
        !string.IsNullOrWhiteSpace(QqGroupNumber)
        || !string.IsNullOrWhiteSpace(QqGroupQrImage)
        || !string.IsNullOrWhiteSpace(WeChatQrImage);
}
