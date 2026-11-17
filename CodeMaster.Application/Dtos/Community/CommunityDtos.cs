using CodeMaster.Core.Dtos;

namespace CodeMaster.Application.Dtos.Community;

public class CommunityCategoryDto
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int TopicCount { get; set; }
}

public class CommunityTopicDto
{
    public long Id { get; set; }

    public long CategoryId { get; set; }

    public string CategoryName { get; set; } = string.Empty;

    public long UserId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string AuthorAvatar { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Excerpt { get; set; } = string.Empty;

    public int Status { get; set; }

    public bool IsPinned { get; set; }

    public bool IsFeatured { get; set; }

    public bool IsSolved => Status == 1;

    public bool IsLocked => Status == 2;

    public int ViewCount { get; set; }

    public int ReplyCount { get; set; }

    public int LikeCount { get; set; }

    public bool Liked { get; set; }

    public bool IsOwner { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? LastReplyTime { get; set; }
}

public class CommunityTopicQueryDto : PagedQueryDto
{
    public long? CategoryId { get; set; }

    public string? Keyword { get; set; }

    /// <summary>active | new | hot | featured | solved</summary>
    public string? Sort { get; set; }
}

public class CreateCommunityTopicDto
{
    public long CategoryId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}

public class UpdateCommunityTopicDto
{
    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}

public class CommunityReplyDto
{
    public long Id { get; set; }

    public long TopicId { get; set; }

    public long UserId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string AuthorAvatar { get; set; } = string.Empty;

    public long? ParentReplyId { get; set; }

    public string? ParentAuthorName { get; set; }

    public string Content { get; set; } = string.Empty;

    public bool IsAccepted { get; set; }

    public int LikeCount { get; set; }

    public bool IsOwner { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? UpdateTime { get; set; }
}

public class CreateCommunityReplyDto
{
    public long TopicId { get; set; }

    public long? ParentReplyId { get; set; }

    public string Content { get; set; } = string.Empty;
}

public class UpdateCommunityReplyDto
{
    public string Content { get; set; } = string.Empty;
}

public class CommunityLikeResultDto
{
    public bool Liked { get; set; }

    public int LikeCount { get; set; }
}

public class CommunityModerationDto
{
    public bool? IsPinned { get; set; }

    public bool? IsFeatured { get; set; }

    public bool? IsLocked { get; set; }
}

public class CommunityContributorDto
{
    public long UserId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string AuthorAvatar { get; set; } = string.Empty;

    public int TopicCount { get; set; }
}

public class CommunityOverviewDto
{
    public int TopicCount { get; set; }

    public int ReplyCount { get; set; }

    public int MemberCount { get; set; }

    public List<CommunityContributorDto> Contributors { get; set; } = new();
}
