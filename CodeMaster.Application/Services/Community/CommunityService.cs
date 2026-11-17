using System.Security.Claims;
using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.Community;
using CodeMaster.Core.Dtos;
using CodeMaster.Core.Exceptions;
using CodeMaster.Core.Repositories;
using CodeMaster.Domain.Entities.Community;
using CodeMaster.Domain.Entities.System;
using Microsoft.AspNetCore.Http;
using SqlSugar;
using Yitter.IdGenerator;

namespace CodeMaster.Application.Services.Community;

public interface ICommunityService
{
    Task<List<CommunityCategoryDto>> GetCategoriesAsync();

    Task<PagedResultDto<CommunityTopicDto>> GetTopicsAsync(CommunityTopicQueryDto query);

    Task<CommunityTopicDto?> GetTopicAsync(long id);

    Task<long> CreateTopicAsync(CreateCommunityTopicDto dto);

    Task UpdateTopicAsync(long id, UpdateCommunityTopicDto dto);

    Task DeleteTopicAsync(long id);

    Task<CommunityLikeResultDto> ToggleTopicLikeAsync(long topicId);

    Task SetTopicFlagsAsync(long topicId, CommunityModerationDto dto);

    Task<List<CommunityReplyDto>> GetRepliesAsync(long topicId);

    Task<long> CreateReplyAsync(CreateCommunityReplyDto dto);

    Task UpdateReplyAsync(long id, UpdateCommunityReplyDto dto);

    Task DeleteReplyAsync(long id);

    Task AcceptReplyAsync(long topicId, long replyId);

    Task<CommunityOverviewDto> GetOverviewAsync();
}

public class CommunityService : ICommunityService
{
    private const int MaxTitleLength = 120;
    private const int MaxTopicContentLength = 20000;
    private const int MaxReplyContentLength = 10000;
    private static readonly TimeSpan TopicCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReplyCooldown = TimeSpan.FromSeconds(15);
    private const int DailyTopicLimit = 10;
    private const int DailyReplyLimit = 60;

    private readonly IRepository<CommunityTopic> _topicRepository;
    private readonly IRepository<CommunityReply> _replyRepository;
    private readonly ISqlSugarClient _db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CommunityService(
        IRepository<CommunityTopic> topicRepository,
        IRepository<CommunityReply> replyRepository,
        ISqlSugarClient db,
        IHttpContextAccessor httpContextAccessor)
    {
        _topicRepository = topicRepository;
        _replyRepository = replyRepository;
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<List<CommunityCategoryDto>> GetCategoriesAsync()
    {
        var categories = await _db.Queryable<CommunityCategory>()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Sort)
            .ToListAsync();

        var result = new List<CommunityCategoryDto>(categories.Count);
        foreach (var category in categories)
        {
            result.Add(new CommunityCategoryDto
            {
                Id = category.Id,
                Name = category.Name,
                Slug = category.Slug,
                Description = category.Description,
                TopicCount = await _db.Queryable<CommunityTopic>()
                    .Where(x => x.CategoryId == category.Id && x.Status != 3)
                    .CountAsync()
            });
        }

        return result;
    }

    public async Task<PagedResultDto<CommunityTopicDto>> GetTopicsAsync(CommunityTopicQueryDto query)
    {
        var pageNum = Math.Max(query.PageNum, 1);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, 50);
        var keyword = query.Keyword?.Trim();

        var q = _db.Queryable<CommunityTopic>()
            .Where(x => x.Status != 3)
            .WhereIF(query.CategoryId.HasValue, x => x.CategoryId == query.CategoryId!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(keyword) && keyword!.Length <= 60,
                x => x.Title.Contains(keyword!) || x.Content.Contains(keyword!));

        switch (query.Sort)
        {
            case "featured":
                q = q.Where(x => x.IsFeatured);
                break;
            case "solved":
                q = q.Where(x => x.Status == 1);
                break;
        }

        q = query.Sort switch
        {
            "new" => q.OrderByDescending(x => x.IsPinned).OrderByDescending(x => x.CreateTime),
            "hot" => q.OrderByDescending(x => x.IsPinned)
                .OrderByDescending(x => x.LikeCount)
                .OrderByDescending(x => x.ReplyCount)
                .OrderByDescending(x => x.ViewCount),
            _ => q.OrderByDescending(x => x.IsPinned)
                .OrderByDescending(x => x.LastReplyTime ?? x.CreateTime)
                .OrderByDescending(x => x.CreateTime)
        };

        var total = await q.CountAsync();
        var topics = await q.ToPageListAsync(pageNum, pageSize);

        return new PagedResultDto<CommunityTopicDto>
        {
            Items = await MapTopicsAsync(topics),
            Total = total,
            PageNum = pageNum,
            PageSize = pageSize
        };
    }

    public async Task<CommunityTopicDto?> GetTopicAsync(long id)
    {
        var topic = await _topicRepository.GetByIdAsync(id);
        if (topic == null || topic.Status == 3)
        {
            return null;
        }

        topic.ViewCount++;
        await _db.Updateable(topic).UpdateColumns(x => new { x.ViewCount }).ExecuteCommandAsync();

        return (await MapTopicsAsync(new List<CommunityTopic> { topic })).FirstOrDefault();
    }

    public async Task<long> CreateTopicAsync(CreateCommunityTopicDto dto)
    {
        var userId = GetCurrentUserId();
        var title = dto.Title?.Trim() ?? string.Empty;
        var content = dto.Content?.Trim() ?? string.Empty;

        if (title.Length < 4)
        {
            throw new BusinessException(400, "标题至少 4 个字。");
        }
        if (title.Length > MaxTitleLength)
        {
            throw new BusinessException(400, $"标题不能超过 {MaxTitleLength} 个字。");
        }
        if (content.Length < 10)
        {
            throw new BusinessException(400, "正文至少 10 个字，方便别人理解你的问题。");
        }
        if (content.Length > MaxTopicContentLength)
        {
            throw new BusinessException(400, $"正文不能超过 {MaxTopicContentLength} 个字符。");
        }

        var categoryExists = await _db.Queryable<CommunityCategory>()
            .Where(x => x.Id == dto.CategoryId && x.IsEnabled)
            .AnyAsync();
        if (!categoryExists)
        {
            throw new BusinessException(400, "版块不存在。");
        }

        var now = DateTime.UtcNow;
        var lastTopic = await _db.Queryable<CommunityTopic>()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreateTime)
            .FirstAsync();
        if (lastTopic != null && now - lastTopic.CreateTime < TopicCooldown)
        {
            var wait = (int)Math.Ceiling((TopicCooldown - (now - lastTopic.CreateTime)).TotalSeconds);
            throw new BusinessException(429, $"发帖有点频繁，请 {wait} 秒后再试。");
        }

        var todayCount = await _db.Queryable<CommunityTopic>()
            .Where(x => x.UserId == userId && x.CreateTime >= now.Date)
            .CountAsync();
        if (todayCount >= DailyTopicLimit)
        {
            throw new BusinessException(429, $"今天已经发布 {DailyTopicLimit} 个主题，明天再来吧。");
        }

        var duplicate = await _db.Queryable<CommunityTopic>()
            .Where(x => x.UserId == userId && x.Title == title && x.CreateTime >= now.AddMinutes(-10))
            .AnyAsync();
        if (duplicate)
        {
            throw new BusinessException(409, "刚刚已经发过同样标题的主题了。");
        }

        var topic = new CommunityTopic
        {
            Id = YitIdHelper.NextId(),
            CategoryId = dto.CategoryId,
            UserId = userId,
            Title = title,
            Content = content,
            Status = 0,
            CreateTime = now,
            CreateUserId = userId,
            LastReplyTime = null
        };

        return await _topicRepository.InsertAsync(topic);
    }

    public async Task UpdateTopicAsync(long id, UpdateCommunityTopicDto dto)
    {
        var userId = GetCurrentUserId();
        var topic = await RequireTopicAsync(id);
        EnsureOwnerOrAdmin(topic.UserId, userId, "只能编辑自己的主题。");

        var title = dto.Title?.Trim() ?? string.Empty;
        var content = dto.Content?.Trim() ?? string.Empty;
        if (title.Length is < 4 or > MaxTitleLength)
        {
            throw new BusinessException(400, $"标题需要 4 到 {MaxTitleLength} 个字。");
        }
        if (content.Length is < 10 or > MaxTopicContentLength)
        {
            throw new BusinessException(400, $"正文需要 10 到 {MaxTopicContentLength} 个字符。");
        }

        topic.Title = title;
        topic.Content = content;
        topic.UpdateTime = DateTime.UtcNow;
        topic.UpdateUserId = userId;
        await _db.Updateable(topic)
            .UpdateColumns(x => new { x.Title, x.Content, x.UpdateTime, x.UpdateUserId })
            .ExecuteCommandAsync();
    }

    public async Task DeleteTopicAsync(long id)
    {
        var userId = GetCurrentUserId();
        var topic = await RequireTopicAsync(id);
        EnsureOwnerOrAdmin(topic.UserId, userId, "只能删除自己的主题。");

        var now = DateTime.UtcNow;
        topic.IsDeleted = true;
        topic.DeleteTime = now;
        topic.DeleteUserId = userId;
        await _db.Updateable(topic)
            .UpdateColumns(x => new { x.IsDeleted, x.DeleteTime, x.DeleteUserId })
            .ExecuteCommandAsync();

        await _db.Updateable<CommunityReply>()
            .SetColumns(x => new CommunityReply
            {
                IsDeleted = true,
                DeleteTime = now,
                DeleteUserId = userId
            })
            .Where(x => x.TopicId == id)
            .ExecuteCommandAsync();
    }

    public async Task<CommunityLikeResultDto> ToggleTopicLikeAsync(long topicId)
    {
        var userId = GetCurrentUserId();
        var topic = await RequireTopicAsync(topicId);

        var existing = await _db.Queryable<CommunityTopicLike>()
            .Where(x => x.TopicId == topicId && x.UserId == userId)
            .FirstAsync();

        var liked = existing == null;
        if (existing == null)
        {
            await _db.Insertable(new CommunityTopicLike
            {
                Id = YitIdHelper.NextId(),
                TopicId = topicId,
                UserId = userId,
                CreateTime = DateTime.UtcNow,
                CreateUserId = userId
            }).ExecuteCommandAsync();
            topic.LikeCount++;
        }
        else
        {
            await _db.Deleteable<CommunityTopicLike>().Where(x => x.Id == existing.Id).ExecuteCommandAsync();
            topic.LikeCount = Math.Max(0, topic.LikeCount - 1);
        }

        await _db.Updateable(topic).UpdateColumns(x => new { x.LikeCount }).ExecuteCommandAsync();
        return new CommunityLikeResultDto { Liked = liked, LikeCount = topic.LikeCount };
    }

    public async Task SetTopicFlagsAsync(long topicId, CommunityModerationDto dto)
    {
        GetCurrentUserId();
        EnsureAdmin();

        var topic = await RequireTopicAsync(topicId);
        if (dto.IsPinned.HasValue) topic.IsPinned = dto.IsPinned.Value;
        if (dto.IsFeatured.HasValue) topic.IsFeatured = dto.IsFeatured.Value;
        if (dto.IsLocked.HasValue)
        {
            topic.Status = dto.IsLocked.Value ? 2 : topic.Status == 1 ? 1 : 0;
        }

        await _db.Updateable(topic)
            .UpdateColumns(x => new { x.IsPinned, x.IsFeatured, x.Status })
            .ExecuteCommandAsync();
    }

    public async Task<List<CommunityReplyDto>> GetRepliesAsync(long topicId)
    {
        var replies = await _db.Queryable<CommunityReply>()
            .Where(x => x.TopicId == topicId)
            .OrderBy(x => x.CreateTime)
            .ToListAsync();
        if (replies.Count == 0)
        {
            return new List<CommunityReplyDto>();
        }

        var currentUserId = GetCurrentUserIdOrNull();
        var userIds = replies.Select(x => x.UserId).Distinct().ToList();
        var users = (await _db.Queryable<SysUser>().Where(x => userIds.Contains(x.Id)).ToListAsync())
            .ToDictionary(x => x.Id);
        var replyAuthors = replies.ToDictionary(x => x.Id, x => x.UserId);

        return replies.Select(x => new CommunityReplyDto
        {
            Id = x.Id,
            TopicId = x.TopicId,
            UserId = x.UserId,
            AuthorName = ResolveName(users, x.UserId),
            AuthorAvatar = users.TryGetValue(x.UserId, out var user) ? user.Avatar ?? string.Empty : string.Empty,
            ParentReplyId = x.ParentReplyId,
            ParentAuthorName = x.ParentReplyId.HasValue && replyAuthors.TryGetValue(x.ParentReplyId.Value, out var parentUserId)
                ? ResolveName(users, parentUserId)
                : null,
            Content = x.Content,
            IsAccepted = x.IsAccepted,
            LikeCount = x.LikeCount,
            IsOwner = currentUserId.HasValue && currentUserId.Value == x.UserId,
            CreateTime = x.CreateTime,
            UpdateTime = x.UpdateTime
        }).ToList();
    }

    public async Task<long> CreateReplyAsync(CreateCommunityReplyDto dto)
    {
        var userId = GetCurrentUserId();
        var content = dto.Content?.Trim() ?? string.Empty;

        if (content.Length < 2)
        {
            throw new BusinessException(400, "回复内容太短了。");
        }
        if (content.Length > MaxReplyContentLength)
        {
            throw new BusinessException(400, $"回复不能超过 {MaxReplyContentLength} 个字符。");
        }

        var topic = await RequireTopicAsync(dto.TopicId);
        if (topic.Status is 2 or 3)
        {
            throw new BusinessException(400, "这个主题已经锁定，无法回复。");
        }

        if (dto.ParentReplyId.HasValue)
        {
            var parentExists = await _db.Queryable<CommunityReply>()
                .Where(x => x.Id == dto.ParentReplyId.Value && x.TopicId == dto.TopicId)
                .AnyAsync();
            if (!parentExists)
            {
                throw new BusinessException(400, "要回复的内容不存在。");
            }
        }

        var now = DateTime.UtcNow;
        var lastReply = await _db.Queryable<CommunityReply>()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreateTime)
            .FirstAsync();
        if (lastReply != null && now - lastReply.CreateTime < ReplyCooldown)
        {
            var wait = (int)Math.Ceiling((ReplyCooldown - (now - lastReply.CreateTime)).TotalSeconds);
            throw new BusinessException(429, $"回复有点频繁，请 {wait} 秒后再试。");
        }

        var todayCount = await _db.Queryable<CommunityReply>()
            .Where(x => x.UserId == userId && x.CreateTime >= now.Date)
            .CountAsync();
        if (todayCount >= DailyReplyLimit)
        {
            throw new BusinessException(429, $"今天回复已经超过 {DailyReplyLimit} 条，明天再来吧。");
        }

        var reply = new CommunityReply
        {
            Id = YitIdHelper.NextId(),
            TopicId = dto.TopicId,
            UserId = userId,
            ParentReplyId = dto.ParentReplyId,
            Content = content,
            CreateTime = now,
            CreateUserId = userId
        };

        var replyId = await _replyRepository.InsertAsync(reply);
        topic.ReplyCount++;
        topic.LastReplyTime = now;
        await _db.Updateable(topic).UpdateColumns(x => new { x.ReplyCount, x.LastReplyTime }).ExecuteCommandAsync();
        return replyId;
    }

    public async Task UpdateReplyAsync(long id, UpdateCommunityReplyDto dto)
    {
        var userId = GetCurrentUserId();
        var reply = await _replyRepository.GetByIdAsync(id) ?? throw new BusinessException(404, "回复不存在。");
        EnsureOwnerOrAdmin(reply.UserId, userId, "只能编辑自己的回复。");

        var content = dto.Content?.Trim() ?? string.Empty;
        if (content.Length is < 2 or > MaxReplyContentLength)
        {
            throw new BusinessException(400, $"回复需要 2 到 {MaxReplyContentLength} 个字符。");
        }

        reply.Content = content;
        reply.UpdateTime = DateTime.UtcNow;
        reply.UpdateUserId = userId;
        await _db.Updateable(reply)
            .UpdateColumns(x => new { x.Content, x.UpdateTime, x.UpdateUserId })
            .ExecuteCommandAsync();
    }

    public async Task DeleteReplyAsync(long id)
    {
        var userId = GetCurrentUserId();
        var reply = await _replyRepository.GetByIdAsync(id) ?? throw new BusinessException(404, "回复不存在。");
        EnsureOwnerOrAdmin(reply.UserId, userId, "只能删除自己的回复。");

        reply.IsDeleted = true;
        reply.DeleteTime = DateTime.UtcNow;
        reply.DeleteUserId = userId;
        await _db.Updateable(reply)
            .UpdateColumns(x => new { x.IsDeleted, x.DeleteTime, x.DeleteUserId })
            .ExecuteCommandAsync();

        var topic = await _db.Queryable<CommunityTopic>().Where(x => x.Id == reply.TopicId).FirstAsync();
        if (topic != null)
        {
            topic.ReplyCount = Math.Max(0, topic.ReplyCount - 1);
            await _db.Updateable(topic).UpdateColumns(x => new { x.ReplyCount }).ExecuteCommandAsync();
        }
    }

    public async Task AcceptReplyAsync(long topicId, long replyId)
    {
        var userId = GetCurrentUserId();
        var topic = await RequireTopicAsync(topicId);
        if (topic.UserId != userId)
        {
            throw new BusinessException(403, "只有主题作者可以采纳回复。");
        }

        var reply = await _replyRepository.GetByIdAsync(replyId);
        if (reply == null || reply.TopicId != topicId)
        {
            throw new BusinessException(404, "回复不存在。");
        }

        var accepting = !reply.IsAccepted;
        if (accepting)
        {
            await _db.Updateable<CommunityReply>()
                .SetColumns(x => new CommunityReply { IsAccepted = false })
                .Where(x => x.TopicId == topicId)
                .ExecuteCommandAsync();
        }

        await _db.Updateable<CommunityReply>()
            .SetColumns(x => new CommunityReply { IsAccepted = accepting })
            .Where(x => x.Id == replyId)
            .ExecuteCommandAsync();

        topic.Status = accepting ? 1 : (topic.Status == 2 ? 2 : 0);
        await _db.Updateable(topic).UpdateColumns(x => new { x.Status }).ExecuteCommandAsync();
    }

    public async Task<CommunityOverviewDto> GetOverviewAsync()
    {
        var overview = new CommunityOverviewDto
        {
            TopicCount = await _db.Queryable<CommunityTopic>().Where(x => x.Status != 3).CountAsync(),
            ReplyCount = await _db.Queryable<CommunityReply>().CountAsync(),
            MemberCount = await _db.Queryable<SysUser>().Where(x => x.Status == 0).CountAsync()
        };

        var recentUserIds = await _db.Queryable<CommunityTopic>()
            .Where(x => x.Status != 3)
            .OrderByDescending(x => x.CreateTime)
            .Take(300)
            .Select(x => x.UserId)
            .ToListAsync();
        var topUserIds = recentUserIds
            .GroupBy(x => x)
            .Select(x => new { UserId = x.Key, Count = x.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToList();
        if (topUserIds.Count > 0)
        {
            var ids = topUserIds.Select(x => x.UserId).ToList();
            var users = (await _db.Queryable<SysUser>().Where(x => ids.Contains(x.Id)).ToListAsync())
                .ToDictionary(x => x.Id);
            overview.Contributors = topUserIds.Select(x => new CommunityContributorDto
            {
                UserId = x.UserId,
                AuthorName = ResolveName(users, x.UserId),
                AuthorAvatar = users.TryGetValue(x.UserId, out var user) ? user.Avatar ?? string.Empty : string.Empty,
                TopicCount = x.Count
            }).ToList();
        }

        return overview;
    }

    private async Task<List<CommunityTopicDto>> MapTopicsAsync(List<CommunityTopic> topics)
    {
        if (topics.Count == 0)
        {
            return new List<CommunityTopicDto>();
        }

        var currentUserId = GetCurrentUserIdOrNull();
        var categoryIds = topics.Select(x => x.CategoryId).Distinct().ToList();
        var userIds = topics.Select(x => x.UserId).Distinct().ToList();
        var categories = (await _db.Queryable<CommunityCategory>().Where(x => categoryIds.Contains(x.Id)).ToListAsync())
            .ToDictionary(x => x.Id);
        var users = (await _db.Queryable<SysUser>().Where(x => userIds.Contains(x.Id)).ToListAsync())
            .ToDictionary(x => x.Id);

        var likedIds = new HashSet<long>();
        if (currentUserId.HasValue)
        {
            var topicIds = topics.Select(x => x.Id).ToList();
            likedIds = (await _db.Queryable<CommunityTopicLike>()
                .Where(x => x.UserId == currentUserId.Value && topicIds.Contains(x.TopicId))
                .Select(x => x.TopicId)
                .ToListAsync()).ToHashSet();
        }

        return topics.Select(x => new CommunityTopicDto
        {
            Id = x.Id,
            CategoryId = x.CategoryId,
            CategoryName = categories.TryGetValue(x.CategoryId, out var category) ? category.Name : "综合讨论",
            UserId = x.UserId,
            AuthorName = ResolveName(users, x.UserId),
            AuthorAvatar = users.TryGetValue(x.UserId, out var user) ? user.Avatar ?? string.Empty : string.Empty,
            Title = x.Title,
            Content = x.Content,
            Excerpt = ToPlainText(x.Content, 180),
            Status = x.Status,
            IsPinned = x.IsPinned,
            IsFeatured = x.IsFeatured,
            ViewCount = x.ViewCount,
            ReplyCount = x.ReplyCount,
            LikeCount = x.LikeCount,
            Liked = likedIds.Contains(x.Id),
            IsOwner = currentUserId.HasValue && currentUserId.Value == x.UserId,
            CreateTime = x.CreateTime,
            LastReplyTime = x.LastReplyTime
        }).ToList();
    }

    private async Task<CommunityTopic> RequireTopicAsync(long id)
    {
        var topic = await _topicRepository.GetByIdAsync(id);
        if (topic == null || topic.Status == 3)
        {
            throw new BusinessException(404, "主题不存在或已隐藏。");
        }

        return topic;
    }

    private static string ResolveName(IReadOnlyDictionary<long, SysUser> users, long userId)
    {
        if (users.TryGetValue(userId, out var user) && !string.IsNullOrWhiteSpace(user.NickName))
        {
            return user.NickName;
        }

        return "CodeMaster 用户";
    }

    private static string ToPlainText(string? markdown, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = Regex.Replace(markdown, "```[\\s\\S]*?```", " ");
        text = Regex.Replace(text, "~~~[\\s\\S]*?~~~", " ");
        text = Regex.Replace(text, "`([^`]*)`", "$1");
        text = Regex.Replace(text, "!\\[[^\\]]*\\]\\([^)]*\\)", " ");
        text = Regex.Replace(text, "\\[([^\\]]*)\\]\\([^)]*\\)", "$1");
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, "^#{1,6}\\s*", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, "^>\\s?", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, "^\\s*[-*+]\\s+", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, "^\\s*\\d+\\.\\s+", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, "[*_~]{1,3}", string.Empty);
        text = Regex.Replace(text, "\\s+", " ").Trim();

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "…";
    }

    private long GetCurrentUserId()
    {
        var userId = GetCurrentUserIdOrNull();
        if (!userId.HasValue)
        {
            throw new BusinessException(401, "请先登录。");
        }

        return userId.Value;
    }

    private long? GetCurrentUserIdOrNull()
    {
        var claim = _httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && long.TryParse(claim.Value, out var userId) ? userId : null;
    }

    private bool IsAdmin()
    {
        return string.Equals(
            _httpContextAccessor.HttpContext?.User.FindFirst("IsAdmin")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureOwnerOrAdmin(long ownerUserId, long currentUserId, string message)
    {
        if (ownerUserId != currentUserId && !IsAdmin())
        {
            throw new BusinessException(403, message);
        }
    }

    private void EnsureAdmin()
    {
        if (!IsAdmin())
        {
            throw new BusinessException(403, "只有管理员可以执行这个操作。");
        }
    }
}
