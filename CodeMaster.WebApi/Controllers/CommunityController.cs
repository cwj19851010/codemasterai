using CodeMaster.Application.Dtos.Community;
using CodeMaster.Application.Services.Community;
using CodeMaster.Core.Common;
using CodeMaster.WebApi.ContentRendering;
using CodeMaster.WebApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeMaster.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CommunityController : ControllerBase
{
    private readonly ICommunityService _communityService;

    public CommunityController(ICommunityService communityService)
    {
        _communityService = communityService;
    }

    [AllowAnonymous]
    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        var result = await _communityService.GetCategoriesAsync();
        return Ok(ApiResponse<object>.Success(result, "OK"));
    }

    [AllowAnonymous]
    [HttpGet("topics")]
    public async Task<IActionResult> GetTopics([FromQuery] CommunityTopicQueryDto query)
    {
        var result = await _communityService.GetTopicsAsync(query);
        return Ok(ApiResponse<object>.Success(result, "OK"));
    }

    [AllowAnonymous]
    [HttpGet("topics/{id:long}")]
    public async Task<IActionResult> GetTopic(long id)
    {
        var result = await _communityService.GetTopicAsync(id);
        return result == null
            ? NotFound(ApiResponse<object>.Fail("Topic not found."))
            : Ok(ApiResponse<object>.Success(result, "OK"));
    }

    [Authorize]
    [HttpPost("topics")]
    public async Task<IActionResult> CreateTopic([FromBody] CreateCommunityTopicDto dto)
    {
        var id = await _communityService.CreateTopicAsync(dto);
        return Ok(ApiResponse<object>.Success(new { id }, "Topic created."));
    }

    [AllowAnonymous]
    [HttpGet("topics/{topicId:long}/replies")]
    public async Task<IActionResult> GetReplies(long topicId)
    {
        var result = await _communityService.GetRepliesAsync(topicId);
        return Ok(ApiResponse<object>.Success(result, "OK"));
    }

    [Authorize]
    [HttpPost("replies")]
    public async Task<IActionResult> CreateReply([FromBody] CreateCommunityReplyDto dto)
    {
        var id = await _communityService.CreateReplyAsync(dto);
        return Ok(ApiResponse<object>.Success(new { id }, "Reply created."));
    }

    [Authorize]
    [HttpPut("topics/{id:long}")]
    public async Task<IActionResult> UpdateTopic(long id, [FromBody] UpdateCommunityTopicDto dto)
    {
        await _communityService.UpdateTopicAsync(id, dto);
        return Ok(ApiResponse.Success("已保存"));
    }

    [Authorize]
    [HttpDelete("topics/{id:long}")]
    public async Task<IActionResult> DeleteTopic(long id)
    {
        await _communityService.DeleteTopicAsync(id);
        return Ok(ApiResponse.Success("已删除"));
    }

    [Authorize]
    [HttpPost("topics/{id:long}/like")]
    public async Task<IActionResult> ToggleTopicLike(long id)
    {
        var result = await _communityService.ToggleTopicLikeAsync(id);
        return Ok(ApiResponse<CommunityLikeResultDto>.Success(result));
    }

    [Authorize]
    [HttpPost("topics/{id:long}/flags")]
    public async Task<IActionResult> SetTopicFlags(long id, [FromBody] CommunityModerationDto dto)
    {
        await _communityService.SetTopicFlagsAsync(id, dto);
        return Ok(ApiResponse.Success("已更新"));
    }

    [Authorize]
    [HttpPut("replies/{id:long}")]
    public async Task<IActionResult> UpdateReply(long id, [FromBody] UpdateCommunityReplyDto dto)
    {
        await _communityService.UpdateReplyAsync(id, dto);
        return Ok(ApiResponse.Success("已保存"));
    }

    [Authorize]
    [HttpDelete("replies/{id:long}")]
    public async Task<IActionResult> DeleteReply(long id)
    {
        await _communityService.DeleteReplyAsync(id);
        return Ok(ApiResponse.Success("已删除"));
    }

    [Authorize]
    [HttpPost("topics/{id:long}/accept/{replyId:long}")]
    public async Task<IActionResult> AcceptReply(long id, long replyId)
    {
        await _communityService.AcceptReplyAsync(id, replyId);
        return Ok(ApiResponse.Success("已更新"));
    }

    [AllowAnonymous]
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] CommunityPreviewRequest request)
    {
        var html = CommunityMarkdown.ToHtml(request.Content);
        return Ok(ApiResponse<object>.Success(new { html }));
    }
}
