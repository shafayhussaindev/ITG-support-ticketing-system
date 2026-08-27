using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Knowledge;
using SupportTicketing.Contracts.Knowledge;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

[ApiController]
[Route("api/v1/knowledge")]
[Produces("application/json")]
public sealed class KnowledgeController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Searches articles the caller is allowed to read.</summary>
    [HttpGet("articles")]
    [HasPermission(Permissions.Knowledge.View)]
    [SwaggerOperation(Summary = "Search articles", Description =
        "Drafts are visible only to their author and to editors. Internal articles are "
        + "filtered out at the database for anyone without staff-level ticket access.")]
    [ProducesResponseType<PagedResult<ArticleListItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ArticleListItemResponse>>> Search(
        [FromQuery] string? search,
        [FromQuery] Guid? categoryId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await dispatcher.QueryAsync(
            new SearchArticlesQuery(search, categoryId, status, page, pageSize), cancellationToken));

    /// <summary>Articles worth offering while a requester describes a problem.</summary>
    [HttpGet("suggestions")]
    [HasPermission(Permissions.Knowledge.View)]
    [SwaggerOperation(Summary = "Suggest articles", Description =
        "Keyword matching over published articles. Deliberately conservative: a wrong "
        + "suggestion at creation time teaches people to ignore the panel.")]
    [ProducesResponseType<IReadOnlyList<ArticleListItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ArticleListItemResponse>>> Suggestions(
        [FromQuery] string? text,
        [FromQuery] Guid? categoryId,
        [FromQuery] int take = 5,
        CancellationToken cancellationToken = default) =>
        Ok(await dispatcher.QueryAsync(new SuggestArticlesQuery(text, categoryId, take), cancellationToken));

    [HttpGet("articles/{id:guid}")]
    [HasPermission(Permissions.Knowledge.View)]
    [SwaggerOperation(Summary = "Get an article")]
    [ProducesResponseType<ArticleDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArticleDetailResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetArticleQuery(id), cancellationToken));

    [HttpGet("articles/{id:guid}/versions")]
    [HasPermission(Permissions.Knowledge.View)]
    [SwaggerOperation(Summary = "Article version history")]
    [ProducesResponseType<IReadOnlyList<ArticleVersionResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ArticleVersionResponse>>> Versions(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetArticleVersionsQuery(id), cancellationToken));

    [HttpPost("articles")]
    [HasPermission(Permissions.Knowledge.Create)]
    [SwaggerOperation(Summary = "Create an article", Description =
        "Always created as a draft. Publishing is a separate, separately permitted act.")]
    [ProducesResponseType<ArticleDetailResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ArticleDetailResponse>> Create(
        [FromBody] CreateArticleRequest request, CancellationToken cancellationToken)
    {
        var article = await dispatcher.SendAsync(new CreateArticleCommand(request), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = article.Id }, article);
    }

    [HttpPut("articles/{id:guid}")]
    [SwaggerOperation(Summary = "Edit an article", Description =
        "Requires knowledge.edit, except on your own unpublished draft, which its author "
        + "may always correct. Judged in the handler rather than by an attribute because "
        + "the answer depends on who wrote the article and whether it is published. "
        + "Writes a new version row so the wording that was live when a ticket was "
        + "resolved stays recoverable.")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ArticleDetailResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ArticleDetailResponse>> Update(
        Guid id, [FromBody] UpdateArticleRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new UpdateArticleCommand(id, request), cancellationToken));

    [HttpPost("articles/{id:guid}/status")]
    [SwaggerOperation(Summary = "Change article status", Description =
        "Publishing requires knowledge.publish and archiving requires knowledge.archive. "
        + "Archiving never deletes: tickets that cite an article must not point at nothing.")]
    [ProducesResponseType<ArticleDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ArticleDetailResponse>> ChangeStatus(
        Guid id, [FromBody] ChangeArticleStatusRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new ChangeArticleStatusCommand(id, request), cancellationToken));

    [HttpPost("articles/{id:guid}/feedback")]
    [HasPermission(Permissions.Knowledge.View)]
    [SwaggerOperation(Summary = "Was this helpful?", Description =
        "One verdict per reader. Changing your mind moves the vote rather than adding another.")]
    [ProducesResponseType<bool>(StatusCodes.Status200OK)]
    public async Task<ActionResult<bool>> Feedback(
        Guid id, [FromBody] ArticleFeedbackRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new RecordArticleFeedbackCommand(id, request), cancellationToken));

    [HttpPost("articles/{id:guid}/view")]
    [HasPermission(Permissions.Knowledge.View)]
    [SwaggerOperation(Summary = "Record a view")]
    [ProducesResponseType<bool>(StatusCodes.Status200OK)]
    public async Task<ActionResult<bool>> View(Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new RecordArticleViewCommand(id), cancellationToken));
}
